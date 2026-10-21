using System;
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

        var opts = new ModuleWriterOptions(module);
        opts.Logger = DummyLogger.NoThrowInstance;
        module.Write(output, opts);
        Console.WriteLine($"Written: {output}");
        return 0;
    }

    // PageDownloadInstall.LoaderInit():
    //   PageLoaderInit(..., New LoaderTask(Of String, DlClientListResult)("关闭下载", Sub() Throw ...), ...)
    // -> PageLoaderInit(..., DlClientListLoader, ...)
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

    // PageDownloadModify.LoadInstanceFinished():
    //   CardMinecraft.Visibility = Collapsed -> Visible
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
}
