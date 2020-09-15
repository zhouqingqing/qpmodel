/*
 * The MIT License (MIT)
 *
 * Copyright (c) 2020 Futurewei Corp.
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 */

using System;
using System.Linq;
using System.Diagnostics;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CSharp;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis.Emit;

using qpmodel.logic;
using qpmodel.physic;

// Code Generation Targets:
//  1. specialization with all possible without breaking code structure.
//  2. can be integrated gradually - we can do one iterator by another.
//    2.1 expr is not included+
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

namespace qpmodel.codegen
{
    class CodeWriter
    {
        static string path_ = "gen.cs";

        static internal void Reset(string header)
        {
            using (StreamWriter file = new StreamWriter(path_))
            {
                file.WriteLine(header);
                file.WriteLine(@"
                using System;
				using System.Collections.Generic;
				using System.Diagnostics;
				using System.IO;

                using qpmodel.physic;
                using qpmodel.utils;
                using qpmodel.logic;
                using qpmodel.test;
                using qpmodel.expr;
                using qpmodel.dml;");

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
            try
            {
                var str = File.ReadAllText(sourcePath);
                var tree = CSharpSyntaxTree.ParseText(str);
                File.WriteAllText(sourcePath, tree.GetRoot().NormalizeWhitespace().ToFullString());
            }
            catch
            {
                Debug.WriteLine("format code failed");
            }
        }

        internal static Assembly Compile()
        {
            // there are builtin CodeDomProvider.CreateProvider("CSharp") or the Nuget one
            //  we use the latter since it suppports newer C# features (but it is way slower)
            // you may encounter IO path issues like: Could not find a part of the path … bin\roslyn\csc.exe
            // solution is to run Nuget console: 
            //     Update-Package Microsoft.CodeDom.Providers.DotNetCompilerPlatform -r
            //
            string source = "gen.cs";
            FromatFile(source);

            // use a provider recognize newer C# features
            var provider = new CSharpCodeProvider();

            // compile it
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(File.ReadAllText(source));
            string assemblyName = Path.GetRandomFileName();
            var refPaths = new[] {
                Assembly.Load(new AssemblyName("qpmodel")).Location,
                typeof(System.Object).GetTypeInfo().Assembly.Location,
                Assembly.Load(new AssemblyName("netstandard")).Location,
                Assembly.Load(new AssemblyName("System.Console")).Location,
                Assembly.Load(new AssemblyName("System.Runtime")).Location,
                Assembly.Load(new AssemblyName("System.Core")).Location,
                Assembly.Load(new AssemblyName("System.Collections")).Location,
                Assembly.Load(new AssemblyName("Microsoft.CSharp")).Location,
                Assembly.Load(new AssemblyName("System.Linq.Expressions")).Location
            };
            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName,
                syntaxTrees: new[] { syntaxTree },
                references: refPaths.Select(r => MetadataReference.CreateFromFile(r)).ToArray(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                            .WithOptimizationLevel(OptimizationLevel.Release));

            // emit code and generate assembly
            using (var ms = new MemoryStream())
            {
                EmitResult result = compilation.Emit(ms);
                if (!result.Success)
                {
                    Console.Error.WriteLine("Compilation failed!");
                    foreach (Diagnostic diagnostic in result.Diagnostics)
                        Console.Error.WriteLine($"{diagnostic.GetMessage()}");
                }
                else
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
                    return assembly;
                }
            }

            return null;
        }

        internal static void Run(Assembly assembly, SQLStatement stmt, ExecContext context)
        {
            // now we can execute it
            var type = assembly.GetType("QueryCode");
            var instance = assembly.CreateInstance("QueryCode");
            var meth = type.GetMember("Run").First() as MethodInfo;
            meth.Invoke(instance, new object[] { stmt, context });
        }
    }
}
