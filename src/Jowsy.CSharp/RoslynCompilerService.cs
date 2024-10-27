﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.DotNet.Interactive.ValueSharing;
using System.Reflection;
using System.Text;

//See https://github.com/RickStrahl/Westwind.Scripting/blob/master/Westwind.Scripting/CSharpScriptExecution.cs#L1254
namespace Jowsy.CSharp
{
    public class RoslynCompilerService
    {

        public ReferenceList References { get; private set; }
        public string GeneratedClassCode { get; private set; }

        public RoslynCompilerService(string? revitVersion)
        {
            if (revitVersion == null)
            {
                throw new ArgumentNullException(nameof(revitVersion));
            }

            References = new ReferenceList();
            AddNetFrameworkDefaultReferences(revitVersion);

            string appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            string addinPath = Path.Combine(appDataFolder, "Autodesk", "Revit", "Addins", revitVersion, "Jowsy.Revit.KernelAddin");
            AddAssembly(Path.Combine(addinPath, "Jowsy.Revit.KernelAddin.dll"));
            AddAssembly(Path.Combine(addinPath, "System.Text.Json.dll"));
        }
        public void AddNetFrameworkDefaultReferences(string revitVersion)
        {
            AddAssembly("mscorlib.dll");
            AddAssembly("System.dll");
            AddAssembly("System.Core.dll");
            AddAssembly("Microsoft.CSharp.dll");
            AddAssembly("System.Net.Http.dll");
            AddAssembly($"C:\\Program Files\\Autodesk\\Revit {revitVersion}\\RevitAPI.dll");
            AddAssembly($"C:\\Program Files\\Autodesk\\Revit {revitVersion}\\RevitAPIUI.dll");
            AddAssembly($"C:\\Program Files\\Autodesk\\Revit {revitVersion}\\RevitAPIIFC.dll");
            AddAssembly($"C:\\Program Files\\Autodesk\\Revit {revitVersion}\\NewtonSoft.Json.dll");

            AddAssembly(typeof(ReferenceList));
        }
        public bool AddAssembly(string assemblyDll)
        {
            if (string.IsNullOrEmpty(assemblyDll)) return false;

            var file = Path.GetFullPath(assemblyDll);

            if (!File.Exists(file))
            {
                // check framework or dedicated runtime app folder
                var path = "C:\\Program Files (x86)\\Reference Assemblies\\Microsoft\\Framework\\.NETFramework\\v4.8\\";
                file = Path.Combine(path, assemblyDll);
                if (!File.Exists(file))
                    return false;
            }

            if (References.Any(r => r.FilePath == file)) return true;

            try
            {
                var reference = MetadataReference.CreateFromFile(file);
                References.Add(reference);
            }
            catch
            {
                return false;
            }

            return true;
        }


        /// <summary>
        /// Compiles a revit addin dll from a C#-script
        /// </summary>
        /// <param name="script">C# code</param>
        /// <param name="KernelValueInfosResolver">a resolver for list of kernelvalueinfos</param>
        /// <returns></returns>
        public async Task<CompilationResults> CompileRevitAddin(string script, bool
                                                                toAssemblyFile,
                                                                Func<Task<KernelValueInfo[]>>? KernelValueInfosResolver)
        {
            var source = SyntaxUtils.BuildClassCode(script);

            var tree = CSharpSyntaxTree.ParseText(source.Trim());

            var root = (CompilationUnitSyntax)tree.GetRoot();

            var tRoot = SyntaxUtils.FixReturn(root);
            var finalTree = CSharpSyntaxTree.Create(tRoot);

            var optimizationLevel = OptimizationLevel.Release;

            var compilation = CSharpCompilation.Create($"revitkernelgenerated-{DateTime.Today.Ticks}")
                .WithOptions(new CSharpCompilationOptions(
                            OutputKind.DynamicallyLinkedLibrary, optimizationLevel: optimizationLevel)
                )
                .AddReferences(References)
                .AddSyntaxTrees(finalTree);

            var diagnostics = compilation.GetDiagnostics().Where(d => d.Id == "CS0103"); //Look for undeclared variables

            GeneratedClassCode = finalTree.ToString();

            if (diagnostics.Any())
            {

                if (KernelValueInfosResolver == null)
                {
                    throw new ArgumentNullException(nameof(KernelValueInfosResolver));
                }

                var valueInfos = await KernelValueInfosResolver();

                var syntax = SyntaxUtils.ResolveUndeclaredVariables(compilation, valueInfos) as CompilationUnitSyntax;

                //New try
                var newTree = CSharpSyntaxTree.Create(syntax);
                compilation = CSharpCompilation.Create($"revitkernelgenerated-{DateTime.Today.Ticks}")
                    .WithOptions(new CSharpCompilationOptions(
                                OutputKind.DynamicallyLinkedLibrary, optimizationLevel: optimizationLevel)
                    )
                    .AddReferences(References)
                    .AddSyntaxTrees(newTree);

                GeneratedClassCode = newTree.ToString();

            }
            //if (SaveGeneratedCode)



            Stream codeStream = null;
            Assembly assembly = null;
            string outputAssembly = null;

            if (toAssemblyFile)
            {
                outputAssembly = Path.Combine(Path.GetTempPath(),
                                  "revitkernel", $"{Path.GetRandomFileName()}.dll");
                if (!Directory.Exists(Path.GetDirectoryName(outputAssembly)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputAssembly));
                }
            }

            codeStream = toAssemblyFile ? new FileStream(outputAssembly, FileMode.Create) :
                                          new MemoryStream();



            using (codeStream)
            {
                EmitResult compilationResult = null;

                compilationResult = compilation.Emit(codeStream);


                // Compilation Error handling
                if (!compilationResult.Success)
                {
                    var sb = new StringBuilder();
                    foreach (var diag in compilationResult.Diagnostics)
                    {

                        sb.AppendLine(diag.ToString());
                    }
                    if (sb != null && sb.Length > 0)
                    {
                        return new CompilationResults()
                        {
                            DiagnosticText = sb.ToString(),
                            Success = false
                        };

                    }

                    if (!toAssemblyFile)
                    {
                        var memStream = (MemoryStream)codeStream;
                        memStream.Seek(0, SeekOrigin.Begin);
                        assembly = Assembly.Load(memStream.ToArray());
                    }

                    return new CompilationResults()
                    {
                        Success = false,
                        DiagnosticText = sb.ToString(),
                    };
                }
            }

            if (toAssemblyFile)
            {
                return new CompilationResults()
                {
                    Success = true,
                    AssemblyPath = outputAssembly
                };
            }
            else
            {
                return new CompilationResults()
                {
                    Success = true,
                    Assembly = assembly
                };
            }


        }
        private Assembly LoadAssemblyFrom(string assemblyFile)
        {
#if NETCORE
            if (AlternateAssemblyLoadContext != null)
            {
                return AlternateAssemblyLoadContext.LoadFromAssemblyPath(assemblyFile);
            }
#endif
            return Assembly.LoadFrom(assemblyFile);
        }
        private Assembly LoadAssembly(byte[] rawAssembly)
        {
            /*#if NETCORE
                        if (AlternateAssemblyLoadContext != null)
                        {
                            return AlternateAssemblyLoadContext.LoadFromStream(new MemoryStream(rawAssembly));
                        }
            #endif*/
            return Assembly.Load(rawAssembly);
        }
        public bool AddAssembly(Type type)
        {
            try
            {
                if (References.Any(r => r.FilePath == type.Assembly.Location))
                    return true;

                var systemReference = MetadataReference.CreateFromFile(type.Assembly.Location);
                References.Add(systemReference);
            }
            catch
            {
                return false;
            }

            return true;
        }
    }
}
