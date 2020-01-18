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

using adb.logic;
using adb.physic;

// Code Generation Targets:
//  1. specialization with all possible without breaking code structure.
//  2. can be integrated gradually - we can do one iterator by another.
//    2.1 expr is not included
//    2.2 some iterators not included
//  3. profiling and debugging intergrated
//
//  Code generation shall do specialization matching the given query plan:
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

namespace adb.codegen
{
    class CodeWriter
    {
        static string path_ = "gen.cs";

        static internal void Reset()
        {
            using (StreamWriter file = new StreamWriter(path_))
            {
                file.WriteLine(@"
                using System;
				using System.Collections.Generic;
				using System.Diagnostics;
				using System.IO;

                using adb.physic;
                using adb.utils;
                using adb.logic;
                using adb.test;
                using adb.expr;
                using adb.dml;");
                file.WriteLine(@"
                // entrance of query execution
                public class QueryCode
                {
                    public static void Run(SQLStatement stmt, ExecContext context)
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
        // format it and write it back to the same file
        internal static void FromatFile(string sourcePath)
        {
            var str = File.ReadAllText(sourcePath);
            var tree = CSharpSyntaxTree.ParseText(str);
            File.WriteAllText(sourcePath, tree.GetRoot().NormalizeWhitespace().ToFullString());
        }

        internal static CompilerResults Compile()
        {
            bool optimize = false;

            // there are builtin CodeDomProvider.CreateProvider("CSharp") or the Nuget one
            //  we use the latter since it suppports newer C# features (but it is way slower)
            // you may encounter IO path issues like: Could not find a part of the path … bin\roslyn\csc.exe
            // solution is to run Nuget console: 
            //     Update-Package Microsoft.CodeDom.Providers.DotNetCompilerPlatform -r
            //
            string source = "gen.cs";
            FromatFile(source);

            // use a provider recognize newer C# features
            var provider = new Microsoft.CodeDom.Providers.DotNetCompilerPlatform.CSharpCodeProvider();

            // compile it
            string[] references = { "adb.exe" };
            CompilerParameters cp = new CompilerParameters(references);
            cp.GenerateInMemory = true;
            cp.GenerateExecutable = false;
            cp.IncludeDebugInformation = !optimize;
            if (optimize) cp.CompilerOptions = "/optimize";
            CompilerResults cr = provider.CompileAssemblyFromFile(cp, source);

            // detect any errors
            if (cr.Errors.Count > 0)
            {
                Console.WriteLine("Errors building {0} into {1}", source, cr.PathToAssembly);
                foreach (CompilerError ce in cr.Errors)
                    Console.WriteLine("  {0}: {1}", ce.ErrorNumber, ce.ErrorText);
                throw new SemanticExecutionException("codegen failed");
            }

            // now we can execute it
            Console.WriteLine("compiled OK");
            return cr;
        }

        internal static void Run(CompilerResults cr, SQLStatement stmt, ExecContext context)
        {
            // now we can execute it
            var assembly = cr.CompiledAssembly;
            var queryCode = assembly.GetType("QueryCode");
            var runmethod = queryCode.GetMethod("Run");
            runmethod.Invoke(null, new object[2] { stmt, context });
        }
    }
}
