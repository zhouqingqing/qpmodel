﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using adb.logic;
using adb.physic;

namespace adb.expr
{
    public class ColumnType
    {
        public Type type_;
        public int len_;
        public ColumnType(Type type, int len) { type_ = type; len_ = len; }

        public bool Compatible(ColumnType type)
        {
            return true;
        }
    }

    public class BoolType : ColumnType
    {
        public BoolType() : base(typeof(bool), 1) { }
        public override string ToString() => $"bool";
    }
    public class IntType : ColumnType
    {
        public IntType() : base(typeof(int), 4) { }
        public override string ToString() => $"int";
    }
    public class DoubleType : ColumnType
    {
        public DoubleType() : base(typeof(double), 8) { }
        public override string ToString() => $"double";
    }
    public class DateTimeType : ColumnType
    {
        public DateTimeType() : base(typeof(DateTime), 8) { }
        public override string ToString() => $"datetime";
    }
    public class TimeSpanType : ColumnType
    {
        public TimeSpanType() : base(typeof(TimeSpan), 8) { }
        public override string ToString() => $"interval";
    }
    public class CharType : ColumnType
    {
        public CharType(int len) : base(typeof(string), len) { }
        public override string ToString() => $"char({len_})";
    }
    public class VarCharType : ColumnType
    {
        public VarCharType(int len) : base(typeof(string), len) { }
        public override string ToString() => $"varchar({len_})";
    }
    public class NumericType : ColumnType
    {
        public int scale_;
        public NumericType(int prec, int scale) : base(typeof(decimal), prec) => scale_ = scale;
        public override string ToString() => $"numeric({len_}, {scale_})";
    }

    public class AnyType : ColumnType
    {
        public AnyType() : base(typeof(object), 8) { }
        public override string ToString() => $"anytype";
    }

    public class RowType : ColumnType
    {
        public RowType() : base(typeof(Row), 8) { }
        public override string ToString() => $"row";
    }

    public abstract class TableRef
    {
        // alias is the first name of the reference
        //  [OK] select b.a1 from a b; 
        //  [FAIL] select a.a1 from a b;
        //
        public string alias_;

        // list of correlated column used in correlated subqueries
        internal readonly List<ColExpr> colRefedBySubq_ = new List<ColExpr>();

        public override string ToString() => $"{alias_}";
        public Expr LocateColumn(string colAlias)
        {
            // TODO: the logic here only uses alias, but not table. Need polish 
            // here to differentiate alias from different tables
            //
            Expr r = null;
            var list = AllColumnsRefs();
            foreach (var v in list)
            {
                if (v.outputName_?.Equals(colAlias) ?? false)
                    if (r is null)
                        r = v;
                    else
                        throw new SemanticAnalyzeException($"ambigous column name {colAlias}");
            }

            return r;
        }

        public List<Expr> AddOuterRefsToOutput(List<Expr> output)
        {
            // for outerrefs, if it is not found in output list, add them there and mark invisible
            colRefedBySubq_.ForEach(x =>
            {
                if (!output.Contains(x))
                {
                    var clone = x.Clone() as ColExpr;
                    clone.isVisible_ = false;
                    clone.isParameter_ = false;
                    output.Add(clone);
                }
            });

            // TBD: DeParameter() can remove some references of colRefedBySubq_ and it is best
            // to find out if *all* references are removed, then we can safely remove this column
            // out of colRefedBySubq_.

            return output;
        }

        public static bool HasColsUsedBySubquries(List<TableRef> tables)
        {
            foreach (var v in tables)
            {
                if (v.colRefedBySubq_.Count > 0)
                    return true;
            }
            return false;
        }
        public abstract List<Expr> AllColumnsRefs();
    }

    // FROM <table> [alias]
    public class BaseTableRef : TableRef
    {
        public string relname_;

        public BaseTableRef(string name, string alias = null)
        {
            Debug.Assert(name != null);
            relname_ = name;
            alias_ = alias ?? relname_;
        }

        public TableDef Table() => Catalog.systable_.Table(relname_);

        public override string ToString()
            => (relname_.Equals(alias_)) ? $"{alias_}" : $"{relname_} as {alias_}";

        public override List<Expr> AllColumnsRefs()
        {
            List<Expr> l = new List<Expr>();
            var columns = Catalog.systable_.TableCols(relname_);
            foreach (var c in columns)
            {
                ColumnDef coldef = c.Value;
                l.Add(new ColExpr(null, alias_, coldef.name_, coldef.type_));
            }

            return l;
        }
    }

    // FROM <filename>
    public class ExternalTableRef : TableRef
    {
        public string filename_;
        public BaseTableRef baseref_;
        public List<Expr> colrefs_;

        public ExternalTableRef( string filename, BaseTableRef baseref, List<Expr> colrefs)
        {
            filename_ = filename.Replace('\'', ' ');
            baseref_ = baseref;
            colrefs_ = colrefs;
            alias_ = baseref.alias_;
        }

        public override string ToString() => filename_;
        public override List<Expr> AllColumnsRefs() => colrefs_;
    }

    public abstract class QueryRef : TableRef
    {
        public SelectStmt query_;

        public QueryRef(SelectStmt query, string alias)
        {
            Debug.Assert(alias != null);
            query_ = query;
            alias_ = alias;
        }

        public override List<Expr> AllColumnsRefs()
        {
            // make a coopy of selection list and replace their tabref as this
            var r = new List<Expr>();

            Debug.Assert(query_.bounded_);
            query_.selection_.ForEach(x =>
            {
                var y = x.Clone();
                y.VisitEachExpr(z =>
                {
                    if (z is ColExpr cz)
                    {
                        cz.tabRef_ = this;
                    }
                });
                r.Add(y);
            });

            // it is actually a waste to return as many as selection: if selection item is 
            // without an alias, there is no way outer layer can references it, thus no need
            // to output them.
            //
            Debug.Assert(r.Count() == query_.selection_.Count());
            return r;
        }
    }

    // FROM <subquery> [alias] [colalias]
    // 
    //  There are some difference with PostgreSQL's handling:
    //  1.  test=# select a4 from (select a3, a4 from a) b(a4);
    //      ERROR:  column reference "a4" is ambiguous
    //    We only look at b(a4), so we are good with this query
    //  2.  PostgreSQL requires a table alias but we don't
    //
    public class FromQueryRef : QueryRef
    {
        List<string> colOutputNames_;

        // select b1+b1 from (select b1*2 from b) a (b1)
        //    mapping_: (b1, b.b1*2)
        // select b1+b1 from (select b1*2 as b1 from b) a
        //    mapping_: (b1, b.b1*2)
        //  This also means a ColRef outside ('b1') may have to replaced by a full 
        //  expression if we want to get rid of fromQuery ('a').
        //
        Dictionary<string, Expr> outputNameMap_;

        public override string ToString() => $"FROM ({alias_})";
        public FromQueryRef(SelectStmt query, string alias, List<string> colOutputNames) : base(query, alias)
        {
            Debug.Assert(alias != null);
            colOutputNames_ = colOutputNames;
        }

        public override List<Expr> AllColumnsRefs()
        {
            if (colOutputNames_.Count == 0)
                return base.AllColumnsRefs();
            else
            {
                // column alias count shall be no more than the selection columns
                if (colOutputNames_.Count > query_.selection_.Count)
                    throw new SemanticAnalyzeException($"more renamed columns than the output columns");

                var r = new List<Expr>();
                for (int i = 0; i < colOutputNames_.Count; i++)
                {
                    var outName = colOutputNames_[i];
                    var x = query_.selection_[i];
                    var y = x.Clone();
                    y.VisitEachExpr(z =>
                    {
                        if (z is ColExpr cz)
                        {
                            cz.tabRef_ = this;
                        }
                    });

                    y.outputName_ = outName;
                    r.Add(y);
                }

                Debug.Assert(r.Count == colOutputNames_.Count);
                return r;
            }
        }

        // map the outside outputName to inside Expr
        public void CreateOutputNameMap()
        {
            List<Expr> inside = query_.selection_;
            List<Expr> outside = AllColumnsRefs();

            // it only expose part of selection to outside ref
            Debug.Assert(outside.Count <= inside.Count);
            outputNameMap_ = new Dictionary<string, Expr>();
            if (colOutputNames_.Count == 0)
                Debug.Assert(inside.Count == outside.Count);
            else
                Debug.Assert(outside.Count == colOutputNames_.Count);
            for (int i = 0; i < outside.Count; i++)
            {
                // no-output-named column can't be referenced outside anyway
                if (outside[i].outputName_ != null)
                {
                    outputNameMap_[outside[i].outputName_] = inside[i];
                    if (inside[i].HasAggFunc())
                        outputNameMap_[outside[i].outputName_] = new ExprRef(inside[i], i);
                }
            }
        }

        public Expr MapOutputName(string name) => outputNameMap_[name];
    }

    // WITH <alias> AS <query>
    public class CTEQueryRef : QueryRef
    {
        public override string ToString() => $"WITH ({alias_})";
        public CTEQueryRef(SelectStmt query, string alias) : base(query, alias)
        {
            Debug.Assert(alias != null);
        }
    }

    public class JoinQueryRef : TableRef
    {
        public List<TableRef> tables_;
        public List<string> joinops_;
        public List<Expr> constraints_;

        public JoinQueryRef(List<TableRef> tables, List<string> joinops, List<Expr> constraints)
        {
            Debug.Assert(tables.Count == joinops.Count + 1);
            Debug.Assert(constraints.Count == joinops.Count);
            tables.ForEach(x => Debug.Assert(!(x is JoinQueryRef)));
            tables_ = tables;
            joinops_ = joinops;
            constraints_ = constraints;
        }

        public override List<Expr> AllColumnsRefs()
        {
            List<Expr> r = new List<Expr>();
            tables_.ForEach(x => r.AddRange(x.AllColumnsRefs()));
            return r;
        }
    }
}
