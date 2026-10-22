using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("usage: patcher <input.exe> <output.exe>");
            return 2;
        }

        string input = args[0];
        string output = args[1];

        var module = ModuleDefMD.Load(input);
        Console.WriteLine($"Assembly: {module.Assembly.FullName}");

        PatchDownloadPage(module);
        PatchModifyPage(module);
        PatchModpackDispatcher(module);
        PatchModpackManualInstall(module);
        PatchStartupNotice(module);

        var opts = new ModuleWriterOptions(module);
        opts.Logger = DummyLogger.NoThrowInstance;
        module.Write(output, opts);
        Console.WriteLine($"Written: {output}");
        return 0;
    }

    // 1. 恢复版本清单加载：PageDownloadInstall.LoaderInit
    //    将"关闭下载"占位加载器替换为官方的 DlClientListLoader
    private static void PatchDownloadPage(ModuleDefMD module)
    {
        var page = module.Find("PCL.PageDownloadInstall", false)
                   ?? throw new InvalidOperationException("PageDownloadInstall not found");
        var method = page.FindMethod("LoaderInit")
                     ?? throw new InvalidOperationException("LoaderInit not found");
        var body = method.Body ?? throw new InvalidOperationException("LoaderInit has no body");

        var downloadMod = module.Find("PCL.ModDownload", false)
                          ?? throw new InvalidOperationException("ModDownload not found");
        var loaderField = downloadMod.FindField("DlClientListLoader")
                          ?? throw new InvalidOperationException("DlClientListLoader not found");

        var ins = body.Instructions;
        int start = -1;
        for (int i = 0; i < ins.Count; i++)
        {
            if (ins[i].OpCode == OpCodes.Ldstr && (string)ins[i].Operand == "关闭下载")
            {
                start = i;
                break;
            }
        }
        if (start < 0) throw new InvalidOperationException("stub loader not found");

        int end = -1;
        for (int i = start; i < ins.Count; i++)
        {
            if (ins[i].OpCode == OpCodes.Newobj && ins[i].Operand is IMethod ctor &&
                ctor.Name == ".ctor" && ctor.DeclaringType?.Name.StartsWith("LoaderTask`2") == true)
            {
                end = i;
                break;
            }
        }
        if (end < 0) throw new InvalidOperationException("stub LoaderTask ctor not found");

        for (int i = end; i >= start; i--) ins.RemoveAt(i);
        ins.Insert(start, Instruction.Create(OpCodes.Ldsfld, loaderField));
        body.OptimizeBranches();
        body.SimplifyBranches();
        body.UpdateInstructionOffsets();

        Console.WriteLine($"Patched PageDownloadInstall.LoaderInit (removed {end - start + 1} instructions)");
    }

    // 2. 恢复版本卡片：PageDownloadModify.LoadInstanceFinished
    //    CardMinecraft.Visibility = Collapsed -> Visible
    private static void PatchModifyPage(ModuleDefMD module)
    {
        var page = module.Find("PCL.PageDownloadModify", false)
                   ?? throw new InvalidOperationException("PageDownloadModify not found");
        var method = page.FindMethod("LoadInstanceFinished")
                     ?? throw new InvalidOperationException("LoadInstanceFinished not found");
        var ins = method.Body.Instructions;

        bool patched = false;
        for (int i = 0; i + 2 < ins.Count; i++)
        {
            if (ins[i].OpCode == OpCodes.Callvirt &&
                ins[i].Operand is IMethod m && m.Name == "get_CardMinecraft" &&
                ins[i + 1].OpCode == OpCodes.Ldc_I4_2 &&
                ins[i + 2].OpCode == OpCodes.Callvirt &&
                ins[i + 2].Operand is IMethod setter && setter.Name == "set_Visibility")
            {
                ins[i + 1] = Instruction.Create(OpCodes.Ldc_I4_0);
                patched = true;
                break;
            }
        }
        if (!patched) throw new InvalidOperationException("CardMinecraft visibility site not found");

        method.Body.UpdateInstructionOffsets();
        Console.WriteLine("Patched PageDownloadModify.LoadInstanceFinished (CardMinecraft -> Visible)");
    }

    // 5. 恢复整合包分派：ModpackInstall(string File, string InstanceName, string Logo)
    //    将 throw "为遵守..." 替换为 Select Case PackType 分派到各 InstallPack* 方法
    private static void PatchModpackDispatcher(ModuleDefMD module)
    {
        var modModpack = module.Find("PCL.ModModpack", false)
                         ?? throw new InvalidOperationException("ModModpack not found");
        var method = modModpack.Methods.FirstOrDefault(m =>
            m.Name == "ModpackInstall" && m.Parameters.Count == 3)
            ?? throw new InvalidOperationException("ModpackInstall(File,...) not found");

        var body = method.Body;
        var ins = body.Instructions;

        // 找到 throw "为遵守相关法律法规..." 的位置
        int throwStart = -1;
        for (int i = 0; i < ins.Count; i++)
        {
            if (ins[i].OpCode == OpCodes.Ldstr &&
                ins[i].Operand is string s &&
                s.StartsWith("为遵守相关法律法规"))
            {
                // 确保后面跟着 newobj Exception::.ctor(string) 和 throw
                if (i + 2 < ins.Count &&
                    ins[i + 1].OpCode == OpCodes.Newobj &&
                    ins[i + 2].OpCode == OpCodes.Throw)
                {
                    throwStart = i;
                    break;
                }
            }
        }
        if (throwStart < 0) throw new InvalidOperationException("modpack restriction throw not found");

        // PackType 是 local[4] (int)
        var packTypeLocal = body.Variables[4];

        // 将所有指向被删除 throw 的分支（leave）重定向到新 switch 的首条指令
        var throwInstr = ins[throwStart];
        var switchStart = Instruction.Create(OpCodes.Ldloc_S, packTypeLocal);
        foreach (var h in body.ExceptionHandlers)
        {
            if (h.HandlerEnd == throwInstr) h.HandlerEnd = switchStart;
        }
        foreach (var instr in ins)
        {
            if (instr.Operand == throwInstr)
                instr.Operand = switchStart;
        }

        // 移除 ldstr / newobj / throw 三条指令
        ins.RemoveAt(throwStart + 2);
        ins.RemoveAt(throwStart + 1);
        ins.RemoveAt(throwStart);

        // 获取 InstallPack* 方法
        var packMethods = new Dictionary<int, IMethod>();
        foreach (var m in modModpack.Methods)
        {
            switch (m.Name)
            {
                case "InstallPackCurseForge": packMethods[0] = m; break;
                case "InstallPackHMCL": packMethods[1] = m; break;
                case "InstallPackMMC": packMethods[2] = m; break;
                case "InstallPackMCBBS": packMethods[3] = m; break;
                case "InstallPackModrinth": packMethods[4] = m; break;
                case "InstallPackLauncherPack": packMethods[9] = m; break;
                case "InstallPackCompress": /* default case */ break;
            }
        }
        var compress = modModpack.Methods.First(m => m.Name == "InstallPackCompress");
        foreach (var kv in new[] { 0, 1, 2, 3, 4, 9 })
            if (!packMethods.ContainsKey(kv))
                throw new InvalidOperationException($"InstallPack for type {kv} not found");

        // 构建 switch 目标
        var case0 = Instruction.Create(OpCodes.Ldarg_0);
        var case1 = Instruction.Create(OpCodes.Ldarg_0);
        var case2 = Instruction.Create(OpCodes.Ldarg_0);
        var case3 = Instruction.Create(OpCodes.Ldarg_0);
        var case4 = Instruction.Create(OpCodes.Ldarg_0);
        var case9 = Instruction.Create(OpCodes.Ldarg_0);
        var caseElse = Instruction.Create(OpCodes.Ldarg_0);

        // switch 目标表：索引 0-9，5/6/7/8 落到 else
        var switchTargets = new[] { case0, case1, case2, case3, case4, caseElse, caseElse, caseElse, caseElse, case9 };

        var newIns = new List<Instruction>
        {
            switchStart,
            Instruction.Create(OpCodes.Switch, switchTargets),
            // default (else) -> InstallPackCompress(File, Archive)
            caseElse,
            Instruction.Create(OpCodes.Ldloc_1),
            Instruction.Create(OpCodes.Call, compress),
            Instruction.Create(OpCodes.Ret),
            // case 0 -> InstallPackCurseForge(File, Archive, ArchiveBaseFolder, InstanceName, Logo)
            case0,
            Instruction.Create(OpCodes.Ldloc_1),
            Instruction.Create(OpCodes.Ldloc_2),
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Ldarg_2),
            Instruction.Create(OpCodes.Call, packMethods[0]),
            Instruction.Create(OpCodes.Ret),
            // case 1 -> InstallPackHMCL(File, Archive, ArchiveBaseFolder)
            case1,
            Instruction.Create(OpCodes.Ldloc_1),
            Instruction.Create(OpCodes.Ldloc_2),
            Instruction.Create(OpCodes.Call, packMethods[1]),
            Instruction.Create(OpCodes.Ret),
            // case 2 -> InstallPackMMC(File, Archive, ArchiveBaseFolder)
            case2,
            Instruction.Create(OpCodes.Ldloc_1),
            Instruction.Create(OpCodes.Ldloc_2),
            Instruction.Create(OpCodes.Call, packMethods[2]),
            Instruction.Create(OpCodes.Ret),
            // case 3 -> InstallPackMCBBS(File, Archive, ArchiveBaseFolder, InstanceName)
            case3,
            Instruction.Create(OpCodes.Ldloc_1),
            Instruction.Create(OpCodes.Ldloc_2),
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Call, packMethods[3]),
            Instruction.Create(OpCodes.Ret),
            // case 4 -> InstallPackModrinth(File, Archive, ArchiveBaseFolder, InstanceName, Logo)
            case4,
            Instruction.Create(OpCodes.Ldloc_1),
            Instruction.Create(OpCodes.Ldloc_2),
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Ldarg_2),
            Instruction.Create(OpCodes.Call, packMethods[4]),
            Instruction.Create(OpCodes.Ret),
            // case 9 -> InstallPackLauncherPack(File, Archive, ArchiveBaseFolder)
            case9,
            Instruction.Create(OpCodes.Ldloc_1),
            Instruction.Create(OpCodes.Ldloc_2),
            Instruction.Create(OpCodes.Call, packMethods[9]),
            Instruction.Create(OpCodes.Ret),
        };

        for (int i = 0; i < newIns.Count; i++)
            ins.Insert(throwStart + i, newIns[i]);
        body.OptimizeBranches();
        body.SimplifyBranches();
        body.UpdateInstructionOffsets();

        Console.WriteLine("Patched ModModpack.ModpackInstall(File,...) (restored dispatcher switch)");
    }

    // 4. 恢复手动安装整合包入口：ModpackInstall()
    //    将仅显示 Hint 的主体替换为：弹出文件选择框 -> 调用 ModpackInstall(File)
    private static void PatchModpackManualInstall(ModuleDefMD module)
    {
        var modModpack = module.Find("PCL.ModModpack", false)
                         ?? throw new InvalidOperationException("ModModpack not found");
        var method = modModpack.Methods.FirstOrDefault(m =>
            m.Name == "ModpackInstall" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("ModpackInstall() not found");

        var body = method.Body;
        var ins = body.Instructions;

        // 清空原有指令
        ins.Clear();

        // 获取所需方法引用
        var selectFile = module.GetMemberRefs().First(mr =>
            mr.Name == "SelectFile" && mr.DeclaringType?.FullName == "MeloongCore.Wpf.Dialogs");

        // OneOf<,> 的 op_Implicit(IEnumerable<(string[],string)>) —— 匹配参数为 IEnumerable 的重载
        var oneOfImplicit = module.GetMemberRefs().First(mr =>
            mr.Name == "op_Implicit" &&
            mr.FullName.Contains("OneOf`2<System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>,System.String>::op_Implicit(System.Collections.Generic.IEnumerable"));

        // Nullable<OneOf<,>>::.ctor(OneOf<,>)
        var nullableCtor = module.GetMemberRefs().First(mr =>
            mr.Name == ".ctor" && mr.FullName.Contains("Nullable") && mr.FullName.Contains("OneOf"));

        var firstOrDefault = module.GetMemberRefs().First(mr =>
            mr.Name == "FirstOrDefault" && mr.DeclaringType?.FullName == "System.Linq.Enumerable" &&
            mr.FullName.Contains("IEnumerable") && !mr.FullName.Contains("Func"));

        var isNullOrEmpty = module.GetMemberRefs().First(mr =>
            mr.Name == "IsNullOrEmpty" && mr.DeclaringType?.FullName == "System.String");

        var modpackInstallFile = modModpack.Methods.First(m =>
            m.Name == "ModpackInstall" && m.Parameters.Count == 3);

        // ValueTuple<string[], string> 的构造（从现有指令中复用类型引用）
        ITypeDefOrRef tupleType = null;
        foreach (var t in module.GetTypes())
        {
            foreach (var mm in t.Methods.Where(x => x.Body != null))
            {
                foreach (var instr in mm.Body.Instructions)
                {
                    if (instr.OpCode == OpCodes.Stelem && instr.Operand is IType tr &&
                        tr.FullName == "System.ValueTuple`2<System.String[],System.String>")
                    {
                        tupleType = tr as ITypeDefOrRef;
                        break;
                    }
                }
                if (tupleType != null) break;
            }
            if (tupleType != null) break;
        }
        if (tupleType == null) throw new InvalidOperationException("ValueTuple<String[],String> type not found");

        var tupleCtor = module.GetMemberRefs().First(mr =>
            mr.Name == ".ctor" &&
            mr.FullName.StartsWith("System.Void System.ValueTuple`2<System.String[],System.String>::.ctor"));

        var ret = Instruction.Create(OpCodes.Ret);

        // 先 push title / false / null，再构造 filter（SelectFile 参数顺序）
        ins.Clear();
        ins.Add(Instruction.Create(OpCodes.Ldstr, "选择整合包压缩文件"));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        ins.Add(Instruction.Create(OpCodes.Ldnull));
        // 构造 filter
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        ins.Add(Instruction.Create(OpCodes.Newarr, tupleType));
        ins.Add(Instruction.Create(OpCodes.Dup));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_3));
        ins.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.String.ToTypeDefOrRef()));
        ins.Add(Instruction.Create(OpCodes.Dup));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        ins.Add(Instruction.Create(OpCodes.Ldstr, "rar"));
        ins.Add(Instruction.Create(OpCodes.Stelem_Ref));
        ins.Add(Instruction.Create(OpCodes.Dup));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        ins.Add(Instruction.Create(OpCodes.Ldstr, "zip"));
        ins.Add(Instruction.Create(OpCodes.Stelem_Ref));
        ins.Add(Instruction.Create(OpCodes.Dup));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_2));
        ins.Add(Instruction.Create(OpCodes.Ldstr, "mrpack"));
        ins.Add(Instruction.Create(OpCodes.Stelem_Ref));
        ins.Add(Instruction.Create(OpCodes.Ldstr, "整合包文件"));
        ins.Add(Instruction.Create(OpCodes.Newobj, tupleCtor));
        ins.Add(Instruction.Create(OpCodes.Stelem, tupleType));
        ins.Add(Instruction.Create(OpCodes.Call, oneOfImplicit));
        ins.Add(Instruction.Create(OpCodes.Newobj, nullableCtor));
        // 调用 SelectFile -> List<string>
        ins.Add(Instruction.Create(OpCodes.Call, selectFile));
        // FirstOrDefault -> string
        ins.Add(Instruction.Create(OpCodes.Call, firstOrDefault));
        // 检查是否为空
        ins.Add(Instruction.Create(OpCodes.Dup));
        ins.Add(Instruction.Create(OpCodes.Call, isNullOrEmpty));
        ins.Add(Instruction.Create(OpCodes.Brtrue_S, ret));
        // 调用 ModpackInstall(File)
        ins.Add(Instruction.Create(OpCodes.Call, modpackInstallFile));
        ins.Add(Instruction.Create(OpCodes.Pop));
        ins.Add(ret);

        body.OptimizeBranches();
        body.SimplifyBranches();
        body.UpdateInstructionOffsets();

        Console.WriteLine("Patched ModModpack.ModpackInstall() (restored manual install entry)");
    }

    // 6. 启动强制公告：在 FormMain 加载后的初始化线程开头插入 PCL 弹窗
    //    MyMsgBox 单按钮 + ForceWait，用户必须点击「我已知悉」才能继续
    private static void PatchStartupNotice(ModuleDefMD module)
    {
        var formMain = module.Find("PCL.FormMain", false)
                       ?? throw new InvalidOperationException("FormMain not found");
        var modMain = module.Find("PCL.ModMain", false)
                      ?? throw new InvalidOperationException("ModMain not found");
        var msgBox = modMain.FindMethod("MyMsgBox")
                     ?? throw new InvalidOperationException("MyMsgBox not found");

        // 定位 FormMain_Loaded 中显示用户协议的后台初始化 lambda
        MethodDef target = null;
        foreach (var m in formMain.Methods)
        {
            if (m.Body == null) continue;
            var list = m.Body.Instructions;
            bool hasEula = list.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "协议授权");
            bool callsMsgBox = list.Any(i => i.OpCode == OpCodes.Call &&
                                             i.Operand is IMethod im && im.Name == "MyMsgBox");
            if (hasEula && callsMsgBox)
            {
                target = m;
                break;
            }
        }
        if (target == null) throw new InvalidOperationException("startup EULA method not found");

        var body = target.Body;
        var ins = body.Instructions;

        if (ins.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == NoticeTitle))
        {
            Console.WriteLine("Startup notice already present, skipped");
            return;
        }

        var notice = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldstr, NoticeCaption),
            Instruction.Create(OpCodes.Ldstr, NoticeTitle),
            Instruction.Create(OpCodes.Ldstr, "我已知悉"),
            Instruction.Create(OpCodes.Ldstr, ""),
            Instruction.Create(OpCodes.Ldstr, ""),
            Instruction.Create(OpCodes.Ldc_I4_1),   // IsWarn
            Instruction.Create(OpCodes.Ldc_I4_1),   // HighLight
            Instruction.Create(OpCodes.Ldc_I4_1),   // ForceWait
            Instruction.Create(OpCodes.Ldnull),     // Button1Action
            Instruction.Create(OpCodes.Ldnull),     // Button2Action
            Instruction.Create(OpCodes.Ldnull),     // Button3Action
            Instruction.Create(OpCodes.Call, msgBox),
            Instruction.Create(OpCodes.Pop)
        };
        for (int i = 0; i < notice.Count; i++) ins.Insert(i, notice[i]);

        if (body.MaxStack < 11) body.MaxStack = 11;
        body.OptimizeBranches();
        body.SimplifyBranches();
        body.UpdateInstructionOffsets();

        Console.WriteLine($"Patched {target.FullName} (inserted third-party notice dialog)");
    }

    private const string NoticeTitle = "第三方修改版公告";

    private const string NoticeCaption =
        "本程序为 Plain Craft Launcher 2 的第三方修改版，与原作者龙腾猫跃及 PCL 官方没有任何关系，不代表官方立场。\r\n\r\n" +
        "本程序仅用于学习交流使用，不得用于任何其他用途。\r\n\r\n" +
        "严禁以任何方式牟利，包括但不限于倒卖、付费分发、捆绑销售、引流变现等行为。\r\n\r\n" +
        "使用本程序产生的全部后果由使用者自行承担。\r\n\r\n" +
        "点击「我已知悉」表示你已阅读并同意以上声明。";
}
