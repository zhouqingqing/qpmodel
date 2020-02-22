# An Relational Optimizer and Executor Modeling

This project models a relational optimizer and executor in c#. The purpose of the modeling is to prepare for a more serious implementation of database optimizer and execution.

## Why C#
Since the major target is optimizer and it is logic centric, so we need a high level language.  After modeling, later we might want to turn it into some C/C++ code, so the language has to be a close relative to them.  C# provides some good features like LINQ, dynamic types to make modeling easy, and it is close enough to C++ (that's why not python).

## Optimizer
The optimizer exercise following constructs:
- Top down/bottom up structure: the optimizer does utilize a top down cascades style optimizer structure but optionally you can choose to use bottom up join order resolver.  We currently use DPccp (["Analysis of Two Existing and One New Dynamic Programming Algorithm"](http://www.vldb.org/conf/2006/p930-moerkotte.pdf)) by G. Moerkotte, et al. It also implments some other join order resolver like DPBushy, mainly for the purpose of correctness verification. A more generic join resolver DPHyper ([Dynamic Programming Strikes Back](https://15721.courses.cs.cmu.edu/spring2017/papers/14-optimizer1/p539-moerkotte.pdf)) is in preparation.
- Subquery decorrelation, cardinality estimation, costing
- The optimizer also expose a DataFrame like interface.
- We verify the optimizer by some unittests and TPCH/DS.

## Executor
In order to verify plan correctness, the project also implements a data centric callback style executor following paper ["How to Architect a query compiler, Revisited"](https://www.cs.purdue.edu/homes/rompf/papers/tahboub-sigmod18.pdf) by R.Y.Tahboub et al. 
- Because we don't have a LMS underneath, so the codeGen is still string template implementation. But thanks for c#'s interpolated string support, the template is easy to read and construct.
- To support incremental codeGen improvement, it supports a generic expression evaluation function which can fallback to the interpreted implementation if codeGen of that specific expression is not supported.
- The executor utilizes Object and dynamic types to simplify implementation. 

## How to Run
Open the project with Visual Studio 2019 community version. Run unnitest.

