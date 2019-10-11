using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections;
using System.Diagnostics;

using Value = System.Int64;

namespace adb
{
    // It carries info needed by expression binding, it includes
    //  - tablerefs, so we can lookup column names
    //  - parent bind context (if it is a subquery)
    // 
    public class BindContext {
        // number of subqueries in the whole query
        static internal int globalSubqCounter_;

        // bounded tables/subqueries: <seq#, tableref>
        readonly internal Dictionary<int, TableRef> boundFrom_ = new Dictionary<int, TableRef>();

        // current statement
        readonly internal SQLStatement stmt_;

        // parent bind context - non-null for subquery only
        readonly internal BindContext parent_; 

        public BindContext(SQLStatement current, BindContext parent) {
            stmt_ = current;
            parent_ = parent;
            if (parent is null)
                globalSubqCounter_ = 0;
        }

        // table APIs
        //
        public void AddTable(TableRef tab) => boundFrom_.Add(boundFrom_.Count, tab);
        public List<TableRef> AllTableRefs() => boundFrom_.Values.ToList();
        public TableRef Table(string alias) => boundFrom_.Values.FirstOrDefault(x => x.alias_.Equals(alias));
        public int TableIndex(string alias) => boundFrom_.Where(x => x.Value.alias_.Equals(alias))?.First().Key ?? -1;

        // column APIs
        //
        TableRef locateByColumnName (string colAlias)
        {
            var result  = AllTableRefs().FirstOrDefault(x => x.LocateColumn(colAlias) != null);
            if (result != AllTableRefs().LastOrDefault(x => x.LocateColumn(colAlias) != null))
                throw new SemanticAnalyzeException("ambigous column name");
            return result;
        }
        public TableRef GetTableRef(string tabAlias, string colAlias)
        {
            if (tabAlias is null)
                return locateByColumnName(colAlias); 
            else
                return Table(tabAlias);
        }
        public int ColumnOrdinal(string tabAlias, string colAlias) {
            int r = -1;
            var lc = Table(tabAlias).AllColumnsRefs();
            for (int i = 0; i < lc.Count; i++)
            {
                if (lc[i].alias_.Equals(colAlias))
                {
                    r = i;
                    break;
                }
            }

            if (Table(tabAlias) is BaseTableRef bt) {
                Debug.Assert(r == Catalog.systable_.Column(bt.relname_, colAlias).ordinal_);
            }

            if (r != -1)
                return r;
            throw new SemanticAnalyzeException($"column not exists {tabAlias}.{colAlias}");
        }
    }

    static public class ExprHelper {
        static internal string tabs(int depth) => new string(' ', depth * 2);

        static public Expr AndListToExpr(List<Expr> andlist)
        {
            Debug.Assert(andlist.Count >= 1);
            if (andlist.Count == 1)
                return andlist[0];
            else
            {
                var andexpr = new LogicAndExpr(andlist[0], andlist[1]);
                for (int i = 2; i < andlist.Count; i++)
                    andexpr.l_ = new LogicAndExpr(andexpr.l_, andlist[i]);
                return andexpr;
            }
        }

        static public List<Expr> AllColExpr(Expr expr, bool includingParameters) {
            var list = new HashSet<Expr>();
            expr.VisitEachExpr(x => {
                if (x is ColExpr xc)
                {
                    if (includingParameters || (!includingParameters && !xc.isOuterRef_))
                        list.Add(xc);
                }
            });

            return list.ToList();
        }

        static public List<TableRef> AllTableRef(Expr expr)
        {
            var list = new HashSet<TableRef>();
            expr.VisitEachExpr(x => {
                if (x is ColExpr xc)
                    list.Add(xc.tabRef_);
            });

            return list.ToList();
        }

        static public string PrintExprWithSubqueryExpanded(Expr expr, int depth) {
            string r = "";
            // append the subquery plan align with expr
            if (expr.HasSubQuery())
            {
                r += "\n";
                expr.VisitEachExpr(x =>
                {
                    if (x is SubqueryExpr sx)
                    {
                        r += tabs(depth + 2) + $"<SubLink> {sx.subqueryid_}\n";
                        Debug.Assert(sx.query_.bindContext_ != null);
                        if (sx.query_.physicPlan_ != null)
                            r += $"{sx.query_.physicPlan_.PrintString(depth + 4)}";
                        else
                            r += $"{sx.query_.logicPlan_.PrintString(depth + 4)}";
                    }
                });
            }

            return r;
        }

        // this is a hack - shall be removed after all optimization process in place
        static public void SubqueryDirectToPhysic(Expr expr)
        {
            // append the subquery plan align with expr
            if (expr.HasSubQuery())
            {
                expr.VisitEachExpr(x =>
                {
                    if (x is SubqueryExpr sx)
                    {
                        sx.query_.physicPlan_ = sx.query_.logicPlan_.DirectToPhysical();
                    }
                });
            }
        }
    }

    public class Expr
    {
        // Expression in selection list can have an alias
        // e.g, a.i+b.i as total
        //
        internal string alias_;

        // we require some columns for query processing but user may not want 
        // them in the final output, so they are marked as invisible.
        // This includes:
        // 1. subquery's outerref 
        //
        internal bool isVisible_ = true;

        // an expression can reference multiple tables
        //      e.g., a.i + b.j > [a.]k => references 2 tables
        // it is a sum of all its children
        //
        public BitArray whichTab_ = new BitArray(256);
        internal bool bounded_ = false;

        public bool EqualTableRefs(BindContext context, TableRef tableRef) {
            Debug.Assert(bounded_);

            // the expression shall access only one table
            if (whichTab_.OfType<bool>().Count(e => e) != 1)
                return false;

            int tindex = context.TableIndex(tableRef.alias_);
            if (tindex != -1 && whichTab_.Get(tindex))
                return true;
            return false;
        }

        // this one uses c# reflection
        // Similar to PlanNode.VisitEachNodeExists()
        //
        public bool VisitEachExprExists(Func<Expr, bool> callback) {
            bool r = callback(this);

            if (!r) {
                var members = GetType().GetFields();
                foreach (var v in members) {
                    if (v.FieldType == typeof(Expr)) {
                        var m = v.GetValue(this) as Expr;
                        if (m.VisitEachExprExists(callback))
                            return true;
                    }
                    // if container<Expr> we shall also handle
                }
                return false;
            }
            return true;
        }

        public void VisitEachExpr(Action<Expr> callback)
        {
            callback(this);
            var members = GetType().GetFields();
            foreach (var v in members)
            {
                if (v.FieldType == typeof(Expr))
                {
                    var m = v.GetValue(this) as Expr;
                    m.VisitEachExpr(callback);
                }
                // if container<Expr> we shall also handle
            }
        }

        public bool HasSubQuery() =>  VisitEachExprExists(e => e is SubqueryExpr);
        public bool IsConst() {
            return !VisitEachExprExists(e => {
                // meaning has non-constantable (or we don't want to waste time try 
                // to figure out if they are constant, say 'select 1' or sin(2))
                //
                bool nonconst = e is ColExpr || e is FuncExpr || e is SubqueryExpr;
                return nonconst;
            });
        }

        // APIs children may implment
        //  - we have to copy out expressions - consider the following query
        // select a2 from(select a3, a1, a2 from a) b
        // PhysicSubquery <b>
        //    Output: b.a2[0]
        //  -> PhysicGet a
        //      Output: a.a2[1]
        // notice b.a2 and a.a2 are the same column but have differenti ordinal.
        // This means we have to copy ColExpr, so its parents, then everything.
        //
        public virtual Expr Clone() => (Expr)this.MemberwiseClone();
        public virtual void Bind(BindContext context) => bounded_ = true;
        public virtual string PrintString(int depth) => ToString();
        public virtual Value Exec(ExecContext context, Row input) => throw new Exception($"{this} subclass shall implment Exec()");
    }

    // Represents "*" or "table.*" - it is not in the tree after Bind(). 
    // To avoid confusion, we implment Expand() instead of Bind().
    //
    public class SelStar : Expr {
        readonly internal string tabAlias_;

        public SelStar(string tabAlias) => tabAlias_ = tabAlias;
        public override string ToString() => tabAlias_ + ".*";

        public override void Bind(BindContext context) => throw new InvalidProgramException("shall be expanded already");
        internal List<Expr> Expand(BindContext context)
        {
            var unbounds = new List<Expr>();
            var exprs = new List<Expr>();
            if (tabAlias_ is null)
            {
                // *
                context.AllTableRefs().ForEach(x => {
                    // subquery's shall be bounded already, and only * from basetable 
                    // are not bounded. We don't have to differentitate them, but I 
                    // just try to be strict.
                    //
                    if (x is FromQueryRef)
                        exprs.AddRange(x.AllColumnsRefs());
                    else
                        unbounds.AddRange(x.AllColumnsRefs());
                });
            }
            else {
                // table.* - you have to find it in current context
                var x = context.Table(tabAlias_);
                if (x is FromQueryRef)
                    exprs.AddRange(x.AllColumnsRefs());
                else
                    unbounds.AddRange(x.AllColumnsRefs());
            }

            unbounds.ForEach(x => x.Bind(context));
            exprs.AddRange(unbounds);
            return exprs;
        }
    }

    // case 1: [A.a1] select A.a1 from (select ...) A;
    //   -- A.a1 tabRef_ is SubqueryRef
    // [A.a1] select * from A where exists (select ... from B where B.b1 = A.a1); 
    //   -- A.a1 is OuterRef_
    //
    public class ColExpr : Expr
    {
        internal string dbName_;
        internal string tabName_;
        readonly internal string colName_;

        // bound: which column in the input row
        internal TableRef tabRef_;
        internal bool isOuterRef_ = false;
        internal int ordinal_;

        // -- execution section --

        public ColExpr(string dbName, string tabName, string colName)
        {
            dbName_ = dbName; tabName_ = tabName; colName_ = colName; alias_ = colName;
        }

        public override void Bind(BindContext context)
        {
            Debug.Assert(tabRef_ is null);

            // if table name is not given, search through all tablerefs
            isOuterRef_ = false;
            tabRef_ = context.GetTableRef(tabName_, colName_);

            // we can't find the column in current context, so it could an outer reference
            if (tabRef_ is null)
            {
                // can't find in my current context, try my ancestors
                BindContext parent = context;
                while ((parent = parent.parent_) != null)
                {
                    tabRef_ = parent.GetTableRef(tabName_, colName_);
                    if (tabRef_ != null)
                    {
                        // we are actually switch the context to parent, whichTab_ is not right ...
                        isOuterRef_ = true;
                        (context.stmt_ as SelectStmt).hasOuterRef_ = true;
                        tabRef_.outerrefs_.Add(this);
                        context = parent;
                        break;
                    }
                }
                if (tabRef_ is null)
                    throw new Exception($"can't bind column '{colName_}' to table");
            }

            Debug.Assert(tabRef_ != null);
            if (tabName_ is null)
                tabName_ = tabRef_.alias_;
            whichTab_.Set(context.TableIndex(tabName_), true);
            ordinal_ = context.ColumnOrdinal(tabName_, colName_);
            bounded_ = true;
        }

        public override int GetHashCode() => (tabName_ + colName_).GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is ColExpr co)
                return co.tabName_.Equals(tabName_) && co.colName_.Equals(colName_);
            return false;
        }
        public override string ToString()
        {
            string para = isOuterRef_ ? "?" : "";
            para += isVisible_ ? "" : "#";
            if (colName_.Equals(alias_))
                return $@"{para}{tabName_}.{colName_}[{ordinal_}]";
            return $@"{para}{tabName_}.{colName_} [{alias_}][{ordinal_}]";
        }
        public override Value Exec(ExecContext context, Row input)
        {
            if (isOuterRef_)
                return context.GetParam(tabRef_, ordinal_);
            else
                return input.values_[ordinal_];
        }
    }

    public class FuncExpr : Expr {
        string func_;

        public FuncExpr(string func) {
            func_ = func;
        }

        public override string ToString() => func_;
    }

    // we can actually put all binary ops in BinExpr class but we want to keep 
    // some special ones (say AND/OR) so we can coding easier
    //
    public class BinExpr : Expr
    {
        public Expr l_;
        public Expr r_;
        public string op_;

        public override Expr Clone()
        {
            var n = (BinExpr)this.MemberwiseClone();
            n.l_ = l_.Clone();
            n.r_ = r_.Clone();
            return n;
        }
        public BinExpr(Expr l, Expr r, string op)
        {
            l_ = l; r_ = r; op_ = op;
        }
		
        public override void Bind(BindContext context)
        {
            l_.Bind(context);
            r_.Bind(context);

            whichTab_ = (l_.whichTab_.Clone() as BitArray).Or(r_.whichTab_);
            bounded_ = true;
        }

        public override string ToString() => $"{l_}{op_}{r_}";

        public override Value Exec(ExecContext context, Row input)
        {
            switch (op_) {
                case "+": return l_.Exec(context, input) + r_.Exec(context, input);
                case "-": return l_.Exec(context, input) - r_.Exec(context, input);
                case "*": return l_.Exec(context, input) * r_.Exec(context, input);
                case "/": return l_.Exec(context, input) / r_.Exec(context, input);
                case ">": return l_.Exec(context, input) > r_.Exec(context, input) ? 1 : 0;
                case ">=": return l_.Exec(context, input) >= r_.Exec(context, input) ? 1 : 0;
                case "<": return l_.Exec(context, input) < r_.Exec(context, input) ? 1 : 0;
                case "<=": return l_.Exec(context, input) <= r_.Exec(context, input) ? 1 : 0;
                case "=": return l_.Exec(context, input) == r_.Exec(context, input) ?1:0;
                default:
                    throw new NotImplementedException();
            }
        }
    }

    public class LogicAndExpr : BinExpr
    {
        public LogicAndExpr(Expr l, Expr r) : base(l, r, " and ") { }

        public override Value Exec(ExecContext context, Row input)
        {
            Value lv = l_.Exec(context, input);
            Value rv = r_.Exec(context, input);

            if (lv == 1 && rv == 1)
                return 1;
            return 0;
        }

        internal List<Expr> BreakToList() {
            var andlist = new List<Expr>();
            for (int i = 0; i < 2; i++)
            {
                Expr e = i == 0 ? l_ : r_;
                if (e is LogicAndExpr ea)
                    andlist.AddRange(ea.BreakToList());
                else
                    andlist.Add(e);
            }

            return andlist;
        }
    }

    public class SubqueryExpr : Expr {
        public SelectStmt query_;
        public int subqueryid_; // bound

        public SubqueryExpr(SelectStmt query) { query_ = query; }
        // don't print the subquery here, it shall be printed by up caller layer for pretty format
        public override string ToString() => $@"@{subqueryid_}";  

        public override void Bind(BindContext context)
        {
        	// subquery id is global, so accumulating at top
			var top = context;
        	while (top.parent_ != null)
				top = top.parent_;
            subqueryid_ = ++BindContext.globalSubqCounter_;

            // query will use a new query context inside
            query_.Bind(context);

            // verify column count after bound because SelStar expansion
            if (query_.selection_.Count != 1)
                throw new SemanticAnalyzeException("subquery must return only one column");
            bounded_ = true;
        }


        public override Value Exec(ExecContext context, Row input)
        {
            Row r = null;
            query_.physicPlan_.Exec(context, l => {
                if (r is null)
                    r = l;
                else
                    throw new SemanticExecutionException("subquery more than one row returned");
                return null;
            });

            if (r is null)
                return Value.MaxValue;
             return r.values_[0];
        }
    }

    public class CTExpr : Expr {
        public string tabName_;
        public List<string> colNames_;
        public SQLStatement query_;

        public CTExpr(string tabName, List<string> colNames, SQLStatement query) {
            tabName_ = tabName; colNames_ = colNames; query_ = query;
        }
    }

    public class OrderTerm : Expr {
        public Expr expr_;
        bool descend_ = false;

        public OrderTerm(Expr expr, bool descend) {
            expr_ = expr; descend_ = descend;
        }
    }

    public class LiteralExpr : Expr
    {
        public SQLiteParser.Literal_valueContext val_;

        public LiteralExpr(SQLiteParser.Literal_valueContext val) => val_ = val;
        public override string ToString() => val_.GetText();
        public override Value Exec(ExecContext context, Row input) => Value.Parse(val_.GetText());
    }

}
