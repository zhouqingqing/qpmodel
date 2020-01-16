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

// code generation
//  code generation shall do specialization matching the given query plan:
//  1. specilization: anything related to the given plan shall be specialized without any 
//     runtime cost. For example, number of columns output, handling of semi/antisemi branches 
//     in join code etc. But keep in mind, we don't want to break the code structure too 
//     much, so we can leave some work to c# compiler for it to figure out. Example:
//      - original code: if (filter is null || filter.Exec(row) is true)
//      since we know filter if null or not by the plan itself,  but we don't know if
//      filter.Exec(row) evlation is true or not depending on the actual data, so we can do 
//      is to generate code without breaking too much structure like:
//      - generated code: $"if ({filter is null} || filter{_}.Exec(row) is true)"
//      So when a filter is not given, it will translated into if (true|| filter2372.Exec(row) is true)
//      then compiler can easily take out this if statement.
//  2. run time decided: anything related to the data can't be specalized. For example, number of 
//     rows, handling of null value of rows etc.
//

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
