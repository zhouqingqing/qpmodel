# An Relational Optimizer and Executor Modeling
This project models a relational optimizer and executor in c#. The purpose of the modeling is to prepare for a more serious implementation of database optimizer and execution.

## Why C#
Since the major target is optimizer and it is logic centric, so it need a high level language.  After modeling, later it might want to turn it into some C/C++ code, so the language has to be a close relative to them.  C# provides some good features like LINQ, dynamic types to make modeling easy, and it is close enough to C++ (that's why not python).

## Optimizer
The optimizer exercise following constructs:
- Top down/bottom up structure: the optimizer does utilize a top down cascades style optimizer structure but optionally you can choose to use bottom up join order resolver.  It currently use DPccp (["Analysis of Two Existing and One New Dynamic Programming Algorithm"](http://www.vldb.org/conf/2006/p930-moerkotte.pdf)) by G. Moerkotte, et al. It also implments some other join order resolver like DPBushy, mainly for the purpose of correctness verification. A more generic join resolver DPHyper ([Dynamic Programming Strikes Back](https://15721.courses.cs.cmu.edu/spring2017/papers/14-optimizer1/p539-moerkotte.pdf)) is in preparation. Implemented join resolver: DPccp, TDBasic, GOO and DPBushy.
- Subquery decorrelation: it follow the ["Unnesting Arbitrary Queries"](https://pdfs.semanticscholar.org/1596/d282b7b6e8723a9780a511c87481df070f7d.pdf) and ["The Complete Story of Joins (in Hyper)"](http://btw2017.informatik.uni-stuttgart.de/slidesandpapers/F1-10-37/paper_web.pdf) by T. Neumann et al. 
- Cardinality estimation, costing: currently this follows text book implementation but due to its locality, later improvements shall have no impact on the architecture.
- The optimizer also expose a DataFrame like interface.
```c#
	// SELECT a1, b1*a1+5 from a join b on b2=a2 where a1>1;
	var a = sqlContext.Read("a");
	var b = sqlContext.Read("b");
	var rows = a.filter("a1>1").join(b, "b2=a2").select("a1", "b1*a1+5").show();
	Assert.AreEqual(string.Join(",", rows), "2,9");
```
- Verify the optimizer by some unittests and TPCH/DS. All TPCH queries are runnable. TPCDS we don't support window function and rolling groups. You can find TPCH plan [here](https://github.com/zhouqingqing/adb/tree/master/test/regress/expect/tpch0001).

## Executor
In order to verify plan correctness, the project also implements a data centric callback style executor following paper ["How to Architect a query compiler, Revisited"](https://www.cs.purdue.edu/homes/rompf/papers/tahboub-sigmod18.pdf) by R.Y.Tahboub et al. 
- It does't use a LMS underneath, the codeGen still string template based. But thanks for c#'s interpolated string support, the template is easy to read and construct side by side with interpreted code.
- To support incremental codeGen improvement, meaning we can ship partial codeGen implmenetation, it supports a generic expression evaluation function which can fallback to the interpreted implementation if that specific expression is not codeGen ready.
- The executor utilizes Object and dynamic types to simplify implementation.

You can see an example generated code for this query [here](https://github.com/zhouqingqing/adb/tree/master/test/gen_example.cs) with generic expression evaluation is in this code snippet:

```c#
	// projection on PhysicHashAgg112: Output: {a.a2}[0]*2,{count(a.a1)}[1],repeat('a',{a.a2}[0]) 
	Row rproj = new Row(3);
	rproj[0] = ((dynamic)r112[0] * (dynamic)2);
	rproj[1] = r112[1];
	rproj[2] = ExprSearch.Locate("74").Exec(context, r112) /*repeat('a',{a.a2}[0])*/;
	r112 = rproj;
```

## How to Play
Open the project with Visual Studio 2019 community version. Run unnitest project. The unittest come up with multiple tests:
- TPCH with small data set
- TPCDS, JoBench only exercise the optimizer
- Other grouped unittest
