using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length == 3 && args[0] == "isolate")
{
    var qaRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../local/qa"));
    // Caller supplies only a copied game inside this project's local/qa tree.
    var managed = Path.GetFullPath(args[1]);
    var profile = Path.GetFullPath(args[2]);
    if (!managed.StartsWith(qaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        !profile.StartsWith(qaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Isolation patch may only touch local/qa copies");
    var file = Path.Combine(managed, "UnityEngine.CoreModule.dll");
    using var module = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { InMemory = true });
    var app = module.MainModule.Types.Single(t => t.FullName == "UnityEngine.Application");
    var getter = app.Methods.Single(m => m.Name == "get_persistentDataPath");
    getter.IsInternalCall = false; getter.IsPInvokeImpl = false;
    getter.Body = new MethodBody(getter);
    // One copied executable may run several isolated processes. The launcher validates
    // this override before setting it; absence always falls back to the QA profile.
    getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "SHAPEZ2_COOP_QA_PROFILE"));
    var environment = new TypeReference("System", "Environment", module.MainModule, module.MainModule.TypeSystem.CoreLibrary);
    var getEnvironment = new MethodReference("GetEnvironmentVariable", module.MainModule.TypeSystem.String, environment) { HasThis = false };
    getEnvironment.Parameters.Add(new ParameterDefinition(module.MainModule.TypeSystem.String));
    getter.Body.Instructions.Add(Instruction.Create(OpCodes.Call, getEnvironment));
    getter.Body.Instructions.Add(Instruction.Create(OpCodes.Dup));
    var returnPath = Instruction.Create(OpCodes.Ret);
    getter.Body.Instructions.Add(Instruction.Create(OpCodes.Brtrue_S, returnPath));
    getter.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
    getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, profile));
    getter.Body.Instructions.Add(returnPath);
    var prefs = module.MainModule.Types.Single(t => t.FullName == "UnityEngine.PlayerPrefs");
    foreach (var method in prefs.Methods.Where(m => m.IsPublic && m.IsStatic))
    {
        method.IsInternalCall = false; method.IsPInvokeImpl = false;
        method.Body = new MethodBody(method);
        if (method.ReturnType.FullName == "System.String")
            method.Body.Instructions.Add(method.Parameters.Count == 2 ? Instruction.Create(OpCodes.Ldarg_1) : Instruction.Create(OpCodes.Ldstr, ""));
        else if (method.ReturnType.FullName == "System.Single")
            method.Body.Instructions.Add(method.Parameters.Count == 2 ? Instruction.Create(OpCodes.Ldarg_1) : Instruction.Create(OpCodes.Ldc_R4, 0f));
        else if (method.ReturnType.FullName == "System.Int32" || method.ReturnType.FullName == "System.Boolean")
            method.Body.Instructions.Add(method.Parameters.Count == 2 ? Instruction.Create(OpCodes.Ldarg_1) : Instruction.Create(OpCodes.Ldc_I4_0));
        else if (method.ReturnType.FullName != "System.Void") throw new InvalidOperationException("Unexpected PlayerPrefs API");
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
    module.Write(file);
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(managed);
    var orchestrationFile = Path.Combine(managed, "Game.Orchestration.dll");
    using var orchestration = AssemblyDefinition.ReadAssembly(orchestrationFile, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
    var helper = orchestration.MainModule.Types.Single(t => t.Name == "GameLoadingHelper").Methods.Single(m => m.Name == "TryInferringLoadingModeFromCurrentLoadedScene");
    var loadingMode = helper.Parameters[0].ParameterType.GetElementType().Resolve();
    int mainMenuMode = Convert.ToInt32(loadingMode.Fields.Single(f => f.Name == "StartInMainMenu").Constant);
    helper.Body = new MethodBody(helper);
    helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
    helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, mainMenuMode));
    helper.Body.Instructions.Add(Instruction.Create(OpCodes.Stind_I4));
    helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
    helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    orchestration.Write(orchestrationFile);
    var bootConfig = Path.GetFullPath(Path.Combine(managed, "..", "boot.config"));
    File.WriteAllLines(bootConfig, File.ReadAllLines(bootConfig)
        .Where(line => !line.StartsWith("single-instance=", StringComparison.Ordinal)));
    Directory.CreateDirectory(profile);
    Console.WriteLine("ISOLATED: persistentDataPath redirected; managed PlayerPrefs disabled in QA copy.");
    return;
}
if (args.Length != 1) throw new ArgumentException("Usage: GameProbe <managed-directory> or isolate <copied-managed> <qa-profile>.");
var directory = Path.GetFullPath(args[0]);
foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
{
    using var assembly = AssemblyDefinition.ReadAssembly(path);
    foreach (var type in Types(assembly.MainModule.Types))
    foreach (var method in type.Methods.Where(m => m.HasBody))
    foreach (var instruction in method.Body.Instructions)
    {
        if (instruction.Operand is MethodReference call &&
            ((call.DeclaringType.FullName == "UnityEngine.Application" && call.Name == "get_persistentDataPath") ||
             call.DeclaringType.FullName == "UnityEngine.PlayerPrefs"))
            Console.WriteLine($"{Path.GetFileName(path)} | {method.FullName} | {call.FullName}");
    }
}
static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
{
    foreach (var type in types) { yield return type; foreach (var nested in Types(type.NestedTypes)) yield return nested; }
}
