using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.CodeDom.Compiler;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;

namespace adb
{
    class ObjectID
    {
        static int id_ = 0;

        internal static void reset() { id_ = 0; }
        internal static int newId() { return ++id_; }
        internal static int curId() { return id_; }
    }

    class CodeWriter
    {
        static string path_ = "gen.cs";

        static internal void Reset()
        {
            using (StreamWriter file = new StreamWriter(path_))
            {
                file.WriteLine(@"using System;
				using System.Collections.Generic;
				using System.Text;
				using System.Threading.Tasks;
				using System.Diagnostics;
				using System.IO;
                using adb;");
                file.WriteLine(@"
                class Program
                {
                    static void Main(string[] args)
                    {");
            }
        }
        static internal void WriteLine(string str)
        {
            using (StreamWriter file = File.AppendText(path_))
            {
                file.WriteLine(str);
            }
        }
    }

    class Compiler
    {
        internal static void Compile()
        {
            // there are builtin CodeDomProvider.CreateProvider("CSharp") or the Nuget one
            //  we use the latter since it suppports newer C# features (but it is way slower)
            // you may encounter IO path issues like: Could not find a part of the path … bin\roslyn\csc.exe
            // solution is to run Nuget console: 
            //     Update-Package Microsoft.CodeDom.Providers.DotNetCompilerPlatform -r
            //
            string source = "gen.cs";
            var provider = new Microsoft.CodeDom.Providers.DotNetCompilerPlatform.CSharpCodeProvider();

            string[] references = { "adb.exe" };
            string exeName = "gen.exe";
            CompilerParameters cp = new CompilerParameters(references);
            cp.GenerateExecutable = true;
            cp.OutputAssembly = exeName;
            cp.CompilerOptions = "/optimize";
            CompilerResults cr = provider.CompileAssemblyFromFile(cp, source);

            // format it
            var str = File.ReadAllText(source);
            var tree = CSharpSyntaxTree.ParseText(str);
            File.WriteAllText(source, tree.GetRoot().NormalizeWhitespace().ToFullString());

            // compile it
            if (cr.Errors.Count > 0)
            {
                Console.WriteLine("Errors building {0} into {1}",
                     source, cr.PathToAssembly);
                foreach (CompilerError ce in cr.Errors)
                {
                    Console.WriteLine("  {0}", ce.ToString());
                    Console.WriteLine();
                }
                throw new SemanticExecutionException("codegen failed");
            }
            else
            {
                Console.WriteLine("compiled OK");
            }
        }
    }
}
