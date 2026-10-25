using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: patcher <input.exe> <output.exe> [manager.dll]");
            return 2;
        }

        string input = args[0];
        string output = args[1];
        string? managerDll = args.Length >= 3 ? args[2] : null;

        var module = ModuleDefMD.Load(input);
        Console.WriteLine($"Assembly: {module.Assembly.FullName}");

        PatchDownloadPage(module);
        PatchModifyPage(module);
        PatchModpackDispatcher(module);
        PatchModpackManualInstall(module);
        PatchStartupNotice(module);

        if (!string.IsNullOrEmpty(managerDll) && File.Exists(managerDll))
        {
            PatchInstanceManagerPage(module, managerDll);
        }
        else
        {
            Console.WriteLine("Skipping instance manager patch (no manager.dll provided)");
        }

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

    // 6. 启动强制公告：双注入点 + 进程内静态标志
    //    任一处被删除，另一处仍会弹出；标志不落盘，删配置/断网均无效
    private static void PatchStartupNotice(ModuleDefMD module)
    {
        var formMain = module.Find("PCL.FormMain", false)
                       ?? throw new InvalidOperationException("FormMain not found");
        var modMain = module.Find("PCL.ModMain", false)
                      ?? throw new InvalidOperationException("ModMain not found");
        var msgBox = modMain.FindMethod("MyMsgBox")
                     ?? throw new InvalidOperationException("MyMsgBox not found");

        // 进程内标志，默认 false；不写入任何配置文件
        var modSecret = module.Find("PCL.ModSecret", false)
                        ?? throw new InvalidOperationException("ModSecret not found");
        var flag = modSecret.FindField("ThirdPartyNoticeShown");
        if (flag == null)
        {
            flag = new FieldDefUser(
                "ThirdPartyNoticeShown",
                new FieldSig(module.CorLibTypes.Boolean),
                FieldAttributes.Public | FieldAttributes.Static);
            modSecret.Fields.Add(flag);
        }

        // 新增专用方法：PCL.ModMain.ShowThirdPartyNotice()
        // 逻辑放在独立方法内，宿主方法只插入一条 call，不改变原有分支与异常处理结构
        var noticeMethod = new MethodDefUser(
            "ShowThirdPartyNotice",
            MethodSig.CreateStatic(module.CorLibTypes.Void),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig)
        {
            Body = new CilBody()
        };
        var nb = noticeMethod.Body;
        nb.MaxStack = 11;
        var ret = Instruction.Create(OpCodes.Ret);
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, flag));
        nb.Instructions.Add(Instruction.Create(OpCodes.Brtrue, ret));
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));   // 置位，避免重复弹出
        nb.Instructions.Add(Instruction.Create(OpCodes.Stsfld, flag));
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldstr, NoticeCaption));
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldstr, NoticeTitle));
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "我已知悉"));
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldstr, ""));
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldstr, ""));
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));   // IsWarn
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));   // HighLight
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));   // ForceWait
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldnull));     // Button1Action
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldnull));     // Button2Action
        nb.Instructions.Add(Instruction.Create(OpCodes.Ldnull));     // Button3Action
        nb.Instructions.Add(Instruction.Create(OpCodes.Call, msgBox));
        nb.Instructions.Add(Instruction.Create(OpCodes.Pop));
        nb.Instructions.Add(ret);
        modMain.Methods.Add(noticeMethod);

        // 注入点 1：FormMain_Loaded 入口
        var loaded = formMain.FindMethod("FormMain_Loaded")
                     ?? throw new InvalidOperationException("FormMain_Loaded not found");
        InjectNoticeCall(loaded, noticeMethod);

        // 注入点 2：显示用户协议的后台初始化 lambda 入口
        MethodDef lambda = null;
        foreach (var m in formMain.Methods)
        {
            if (m.Body == null) continue;
            var list = m.Body.Instructions;
            bool hasEula = list.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "协议授权");
            bool callsMsgBox = list.Any(i => i.OpCode == OpCodes.Call &&
                                             i.Operand is IMethod im && im.Name == "MyMsgBox");
            if (hasEula && callsMsgBox)
            {
                lambda = m;
                break;
            }
        }
        if (lambda == null) throw new InvalidOperationException("startup EULA method not found");
        InjectNoticeCall(lambda, noticeMethod);
    }

    private static void InjectNoticeCall(MethodDef method, IMethod noticeMethod)
    {
        var body = method.Body ?? throw new InvalidOperationException($"{method.FullName} has no body");
        if (body.Instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand == noticeMethod))
        {
            Console.WriteLine($"Startup notice call already present in {method.FullName}, skipped");
            return;
        }
        body.Instructions.Insert(0, Instruction.Create(OpCodes.Call, noticeMethod));
        body.UpdateInstructionOffsets();
        Console.WriteLine($"Patched {method.FullName} (injected notice check)");
    }

    // ===================== 实例管理中心（嵌入 PCL2.Manager.dll）=====================

    // 7. 实例管理中心：将 PCL2.Manager.dll 作为嵌入资源写入 EXE，
    //    注入 AssemblyResolve 运行时加载，并把 PageGet 的 DownloadResourcePack 分支
    //    替换为返回 PCL2.Manager.ManagerPage（版本设置页内置管理中心）
    private static void PatchInstanceManagerPage(ModuleDefMD module, string managerDllPath)
    {
        byte[] managerBytes = File.ReadAllBytes(managerDllPath);
        var managerModule = ModuleDefMD.Load(managerBytes);
        string asmName = managerModule.Assembly.Name.String;
        var asmVersion = managerModule.Assembly.Version;
        Console.WriteLine($"Manager assembly: {asmName} v{asmVersion} ({managerBytes.Length} bytes)");

        // 1. 嵌入资源
        string resName = asmName + ".dll";
        var existingRes = module.Resources.FirstOrDefault(r => r.Name == resName);
        if (existingRes != null) module.Resources.Remove(existingRes);
        module.Resources.Add(new EmbeddedResource(resName, managerBytes, ManifestResourceAttributes.Public));

        // 2. 程序集引用：通过 UpdateRowId 注册到模块的 AssemblyRef 表
        var asmRef = new AssemblyRefUser(new AssemblyNameInfo(asmName) { Version = asmVersion });
        module.UpdateRowId(asmRef);

        // 3. ManagerPage 类型与构造函数引用
        var managerPageType = new TypeRefUser(module, "PCL2.Manager", "ManagerPage", asmRef);
        var managerCtor = new MemberRefUser(module, ".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void,
                module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String),
            managerPageType);

        // 4. 注入 AssemblyResolve 处理程序到 ModMain
        var modMain = module.Find("PCL.ModMain", false)
                      ?? throw new InvalidOperationException("ModMain not found");
        var oldHandler = modMain.FindMethod("ResolveManagerAssembly");
        if (oldHandler != null) modMain.Methods.Remove(oldHandler);
        var resolveMethod = CreateResolveHandler(module, asmName);
        modMain.Methods.Add(resolveMethod);

        // 5. 在 FormMain_Loaded 注册 AssemblyResolve
        RegisterAssemblyResolve(module, resolveMethod);

        // 6. 修改 PageGet
        PatchPageGetForManager(module, managerCtor);

        Console.WriteLine("Patched instance manager page (embedded + AssemblyResolve + PageGet)");
    }

    // 构造 ModMain.ResolveManagerAssembly(object, ResolveEventArgs)
    // 当运行时请求 PCL2.Manager 程序集时，从嵌入资源读取并 Assembly.Load(byte[])
    private static MethodDef CreateResolveHandler(ModuleDefMD module, string asmName)
    {
        var corLib = module.CorLibTypes.AssemblyRef;
        var assemblyType = new TypeRefUser(module, "System.Reflection", "Assembly", corLib);
        var streamType = new TypeRefUser(module, "System.IO", "Stream", corLib);
        var resolveEventArgsType = new TypeRefUser(module, "System", "ResolveEventArgs", corLib);
        var stringType = new TypeRefUser(module, "System", "String", corLib);
        var byteType = new TypeRefUser(module, "System", "Byte", corLib);
        var byteArray = new SZArraySig(byteType.ToTypeSig());

        var getExecutingAssembly = new MemberRefUser(module, "GetExecutingAssembly",
            MethodSig.CreateStatic(assemblyType.ToTypeSig()), assemblyType);
        var getManifestResourceStream = new MemberRefUser(module, "GetManifestResourceStream",
            MethodSig.CreateInstance(streamType.ToTypeSig(), module.CorLibTypes.String), assemblyType);
        var loadBytes = new MemberRefUser(module, "Load",
            MethodSig.CreateStatic(assemblyType.ToTypeSig(), byteArray), assemblyType);
        var streamGetLength = new MemberRefUser(module, "get_Length",
            MethodSig.CreateInstance(module.CorLibTypes.Int64), streamType);
        var streamRead = new MemberRefUser(module, "Read",
            MethodSig.CreateInstance(module.CorLibTypes.Int32, byteArray, module.CorLibTypes.Int32, module.CorLibTypes.Int32),
            streamType);
        var getName = new MemberRefUser(module, "get_Name",
            MethodSig.CreateInstance(module.CorLibTypes.String), resolveEventArgsType);
        var startsWith = new MemberRefUser(module, "StartsWith",
            MethodSig.CreateInstance(module.CorLibTypes.Boolean, module.CorLibTypes.String), stringType);

        var method = new MethodDefUser(
            "ResolveManagerAssembly",
            MethodSig.CreateStatic(assemblyType.ToTypeSig(), module.CorLibTypes.Object, resolveEventArgsType.ToTypeSig()),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig)
        { Body = new CilBody() };

        var body = method.Body;
        body.MaxStack = 4;
        var streamLocal = new Local(streamType.ToTypeSig());
        var bufferLocal = new Local(byteArray);
        var lenLocal = new Local(module.CorLibTypes.Int32);
        body.Variables.Add(streamLocal);
        body.Variables.Add(bufferLocal);
        body.Variables.Add(lenLocal);

        var retNull = Instruction.Create(OpCodes.Ldnull);
        var ret = Instruction.Create(OpCodes.Ret);

        // if (args.Name.StartsWith(asmName)) { ... } else return null;
        body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getName));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, asmName));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, startsWith));
        body.Instructions.Add(Instruction.Create(OpCodes.Brfalse_S, retNull));

        // Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(asmName + ".dll");
        body.Instructions.Add(Instruction.Create(OpCodes.Call, getExecutingAssembly));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, asmName + ".dll"));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getManifestResourceStream));
        body.Instructions.Add(Instruction.Create(OpCodes.Stloc, streamLocal));

        // int len = (int)stream.Length;
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, streamLocal));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, streamGetLength));
        body.Instructions.Add(Instruction.Create(OpCodes.Conv_I4));
        body.Instructions.Add(Instruction.Create(OpCodes.Stloc, lenLocal));

        // byte[] buffer = new byte[len];
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lenLocal));
        body.Instructions.Add(Instruction.Create(OpCodes.Newarr, byteType));
        body.Instructions.Add(Instruction.Create(OpCodes.Stloc, bufferLocal));

        // stream.Read(buffer, 0, len);
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, streamLocal));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, bufferLocal));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lenLocal));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, streamRead));
        body.Instructions.Add(Instruction.Create(OpCodes.Pop));

        // return Assembly.Load(buffer);
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, bufferLocal));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, loadBytes));
        body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        body.Instructions.Add(retNull);
        body.Instructions.Add(ret);

        body.OptimizeBranches();
        body.UpdateInstructionOffsets();
        return method;
    }

    // 在 FormMain_Loaded 开头注册 AppDomain.CurrentDomain.AssemblyResolve += ResolveManagerAssembly
    private static void RegisterAssemblyResolve(ModuleDefMD module, MethodDef resolveMethod)
    {
        var formMain = module.Find("PCL.FormMain", false)
                       ?? throw new InvalidOperationException("FormMain not found");
        var loaded = formMain.FindMethod("FormMain_Loaded")
                     ?? throw new InvalidOperationException("FormMain_Loaded not found");

        var corLib = module.CorLibTypes.AssemblyRef;
        var appDomainType = new TypeRefUser(module, "System", "AppDomain", corLib);
        var resolveEventHandlerType = new TypeRefUser(module, "System", "ResolveEventHandler", corLib);

        var currentDomainGet = new MemberRefUser(module, "get_CurrentDomain",
            MethodSig.CreateStatic(appDomainType.ToTypeSig()), appDomainType);
        var addAssemblyResolve = new MemberRefUser(module, "add_AssemblyResolve",
            MethodSig.CreateInstance(module.CorLibTypes.Void, resolveEventHandlerType.ToTypeSig()), appDomainType);
        var resolveEventHandlerCtor = new MemberRefUser(module, ".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.Object, module.CorLibTypes.IntPtr),
            resolveEventHandlerType);

        var body = loaded.Body;
        if (body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt &&
                                       i.Operand is IMethod m && m.Name == "add_AssemblyResolve"))
        {
            Console.WriteLine("AssemblyResolve already registered in FormMain_Loaded, skipped");
            return;
        }

        var ins = body.Instructions;
        ins.Insert(0, Instruction.Create(OpCodes.Call, currentDomainGet));
        ins.Insert(1, Instruction.Create(OpCodes.Ldnull));
        ins.Insert(2, Instruction.Create(OpCodes.Ldftn, resolveMethod));
        ins.Insert(3, Instruction.Create(OpCodes.Newobj, resolveEventHandlerCtor));
        ins.Insert(4, Instruction.Create(OpCodes.Callvirt, addAssemblyResolve));

        body.UpdateInstructionOffsets();
        Console.WriteLine("Patched FormMain_Loaded (registered AssemblyResolve handler)");
    }

    // 将 PageInstanceLeft.PageGet 的 DownloadResourcePack 分支替换为 new ManagerPage(...)
    private static void PatchPageGetForManager(ModuleDefMD module, IMethod managerCtor)
    {
        var pageInstanceLeft = module.Find("PCL.PageInstanceLeft", false)
                               ?? throw new InvalidOperationException("PageInstanceLeft not found");
        var pageGet = pageInstanceLeft.FindMethod("PageGet")
                      ?? throw new InvalidOperationException("PageGet not found");

        var ins = pageGet.Body.Instructions;

        // 找到 newobj PageInstanceExport::.ctor
        int exportCtorIdx = -1;
        for (int i = 0; i < ins.Count; i++)
        {
            if (ins[i].OpCode == OpCodes.Newobj &&
                ins[i].Operand is IMethod m && m.DeclaringType?.Name == "PageInstanceExport")
            {
                exportCtorIdx = i;
                break;
            }
        }
        if (exportCtorIdx < 0) throw new InvalidOperationException("PageInstanceExport ctor not found in PageGet");

        // 找到 switch 指令
        int switchIdx = -1;
        for (int i = 0; i < exportCtorIdx; i++)
        {
            if (ins[i].OpCode == OpCodes.Switch) { switchIdx = i; break; }
        }
        if (switchIdx < 0) throw new InvalidOperationException("switch not found in PageGet");

        var targets = (Instruction[])ins[switchIdx].Operand;

        // 确定 case 入口（switch 目标中 <= exportCtorIdx 且最接近的）
        int caseEntryIdx = -1;
        Instruction? caseEntry = null;
        foreach (var t in targets)
        {
            int idx = ins.IndexOf(t);
            if (idx >= 0 && idx <= exportCtorIdx && idx > caseEntryIdx)
            { caseEntryIdx = idx; caseEntry = t; }
        }
        // switch 之后的 fall-through 也可能是 default 入口
        int afterSwitch = switchIdx + 1;
        if (afterSwitch <= exportCtorIdx && afterSwitch > caseEntryIdx)
        { caseEntryIdx = afterSwitch; caseEntry = ins[afterSwitch]; }
        if (caseEntry == null) throw new InvalidOperationException("case entry not found");

        // 确定 case 结束：下一个 switch 目标（case 之间没有独立 ret，统一跳转到末尾的 ldloc.0; ret）
        int caseEndIdx = ins.Count;
        foreach (var t in targets)
        {
            int idx = ins.IndexOf(t);
            if (idx > caseEntryIdx && idx < caseEndIdx) caseEndIdx = idx;
        }
        if (caseEndIdx <= caseEntryIdx)
            throw new InvalidOperationException("case end not found for DownloadResourcePack");

        // 获取所需引用
        var modMinecraft = module.Find("PCL.ModMinecraft", false)
                           ?? throw new InvalidOperationException("ModMinecraft not found");
        var mcFolderSelectedGet = modMinecraft.FindMethod("get_McFolderSelected")
                                  ?? throw new InvalidOperationException("get_McFolderSelected not found");
        var instanceField = pageInstanceLeft.FindField("Instance")
                            ?? throw new InvalidOperationException("Instance field not found");
        var mcInstanceType = module.Find("PCL.ModMinecraft/McInstance", false)
                             ?? throw new InvalidOperationException("McInstance not found");
        var pathIndieGet = mcInstanceType.FindMethod("get_PathIndie")
                           ?? throw new InvalidOperationException("get_PathIndie not found");
        var nameGet = mcInstanceType.FindMethod("get_Name")
                      ?? throw new InvalidOperationException("get_Name not found");

        // 构造新指令
        var newInstructions = new List<Instruction>
        {
            Instruction.Create(OpCodes.Call, mcFolderSelectedGet),
            Instruction.Create(OpCodes.Ldsfld, instanceField),
            Instruction.Create(OpCodes.Callvirt, pathIndieGet),
            Instruction.Create(OpCodes.Ldsfld, instanceField),
            Instruction.Create(OpCodes.Callvirt, nameGet),
            Instruction.Create(OpCodes.Newobj, managerCtor),
            Instruction.Create(OpCodes.Ret),
        };

        // 把 switch 目标重定向到新的首条指令
        for (int i = 0; i < targets.Length; i++)
            if (targets[i] == caseEntry) targets[i] = newInstructions[0];

        // 移除旧 case 指令（从后往前删，范围 [caseEntryIdx, caseEndIdx)）
        for (int i = caseEndIdx - 1; i >= caseEntryIdx; i--) ins.RemoveAt(i);

        // 插入新指令
        for (int i = 0; i < newInstructions.Count; i++)
            ins.Insert(caseEntryIdx + i, newInstructions[i]);

        pageGet.Body.OptimizeBranches();
        pageGet.Body.SimplifyBranches();
        pageGet.Body.UpdateInstructionOffsets();

        Console.WriteLine($"Patched PageInstanceLeft.PageGet (DownloadResourcePack -> ManagerPage, replaced {caseEndIdx - caseEntryIdx} -> {newInstructions.Count} instructions)");
    }

    private const string NoticeTitle = "第三方修改版公告";

    private const string NoticeCaption =
        "本程序为 Plain Craft Launcher 2 的第三方修改版，与原作者龙腾猫跃及 PCL 官方没有任何关系，不代表官方立场。\r\n\r\n" +
        "本程序仅用于学习交流使用，不得用于任何其他用途。\r\n\r\n" +
        "严禁以任何方式牟利，包括但不限于倒卖、付费分发、捆绑销售、引流变现等行为。\r\n\r\n" +
        "使用本程序产生的全部后果由使用者自行承担。\r\n\r\n" +
        "点击「我已知悉」表示你已阅读并同意以上声明。";
}
