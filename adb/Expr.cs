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
    // it carries global info needed by expression binding
    public class BindContext {

        // Local section
        //      these fields are local to current subquery
        // -----------------------------

        // bounded tables/subqueries
        internal Dictionary<int, TableRef> boundFrom_ = new Dictionary<int, TableRef>();
        // parent bind context - non-null for subquery only
        internal BindContext parent_;

        // Global seciton
        //      fields maintained across subquery boundary
        // -----------------------------

        // number of subqueries in the whole query
        internal int nSubqueries = 0;

        public BindContext(BindContext parent) {
            parent_ = parent;
            if (parent != null)
                nSubqueries = parent.nSubqueries;
        }

        // table APIs
        public void AddTable(TableRef tab) => boundFrom_.Add(boundFrom_.Count, tab);
        public List<TableRef> EnumTableRefs() => boundFrom_.Values.ToList();
        public TableRef Table(string alias) => boundFrom_.Values.FirstOrDefault(x => x.alias_.Equals(alias));
        public int TableIndex(string alias) => boundFrom_.Where(x => x.Value.alias_.Equals(alias))?.First().Key ?? -1;

        // column APIs
        TableRef locateByColumnName (string colAlias)
        {
            var result  = EnumTableRefs().FirstOrDefault(x => x.LocateColumn(colAlias) != null);
            if (result != EnumTableRefs().LastOrDefault(x => x.LocateColumn(colAlias) != null))
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
            var lc = Table(tabAlias).GenerateAllColumnsRefs();
            for (int i = 0; i < lc.Count; i++)
            {
                if (lc[i].alias_.Equals(colAlias))
                {
                    r = i;
                    break;
                }
            }

            if (Table(tabAlias) is BaseTableRef bt) {
                Debug.Assert(r == Catalog.systable_.Column(tabAlias, colAlias).ordinal_);
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

        static public List<ColExpr> EnumAllColExpr(Expr expr, bool includingParameters) {
            var list = new HashSet<ColExpr>();
            expr.VisitEachExpr(x => {
                if (x is ColExpr xc)
                {
                    if (includingParameters || (!includingParameters && !xc.isOuterRef_))
                        list.Add(xc);
                }
                return false;
            });

            return list.ToList();
        }

        static public List<TableRef> EnumAllTableRef(Expr expr)
        {
            var list = new HashSet<TableRef>();
            expr.VisitEachExpr(x => {
                if (x is ColExpr xc)
                    list.Add(xc.tabRef_);
                return false;
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
                        Debug.Assert(sx.query_.cores_[0].BinContext() != null);
                        if (sx.query_.GetPhysicPlan() != null)
                            r += $"{sx.query_.GetPhysicPlan().PrintString(depth + 2)}";
                        else
                            r += $"{sx.query_.GetLogicPlan().PrintString(depth + 2)}";
                    }
                    return false;
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
                        sx.query_.physicPlan_ = sx.query_.GetLogicPlan().DirectToPhysical();
                    }
                    return false;
                });
            }
        }
    }

    public class Expr
    {
        // a.i+b.i as total
        internal string alias_;

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
        public bool VisitEachExpr(Func<Expr, bool> callback) {
            bool r = callback(this);

            if (!r) {
                var members = GetType().GetFields();
                foreach (var v in members) {
                    if (v.FieldType == typeof(Expr)) {
                        var m = v.GetValue(this) as Expr;
                        if (m.VisitEachExpr(callback))
                            return true;
                    }
                    // if container<Expr> we shall also handle
                }
                return false;
            }
            return true;
        }

        public bool HasSubQuery() =>  VisitEachExpr(e => e is SubqueryExpr);
        public bool IsConst() {
            return !VisitEachExpr(e => {
                // meaning has non-constantable (or we don't want to waste time try 
                // to figure out if they are constant, say 'select 1' or sin(2))
                //
                bool nonconst = e is ColExpr || e is FuncExpr || e is SubqueryExpr;
                return nonconst;
            });
        }

        // APIs children may implment
        public virtual void Bind(BindContext context) => bounded_ = true;
        public virtual string PrintString(int depth) => ToString();
        public virtual Value Exec(Row input) => throw new Exception($"{this} subclass shall implment Exec()");
    }

    // Represents "*" or "table.*" - it is not in the tree after Bind(). 
    // To avoid confusion, we implment Expand() instead of Bind().
    //
    public class SelStar : Expr {
        internal string tabAlias_;

        public SelStar(string tabAlias) => tabAlias_ = tabAlias;
        public override string ToString() => tabAlias_ + ".*";

        internal List<Expr> Expand(BindContext context)
        {
            var unbounds = new List<Expr>();
            var exprs = new List<Expr>();
            if (tabAlias_ is null)
            {
                // *
                context.EnumTableRefs().ForEach(x => {
                    // subquery's shall be bounded already, and only * from basetable 
                    // are not bounded. We don't have to differentitate them, but I 
                    // just try to be strict.
                    //
                    if (x is SubqueryRef)
                        exprs.AddRange(x.GenerateAllColumnsRefs());
                    else
                        unbounds.AddRange(x.GenerateAllColumnsRefs());
                });
            }
            else {
                // table.* - you have to find it in current context
                var x = context.Table(tabAlias_);
                if (x is SubqueryRef)
                    exprs.AddRange(x.GenerateAllColumnsRefs());
                else
                    unbounds.AddRange(x.GenerateAllColumnsRefs());
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
        internal string colName_;

        // bound: which column in the input row
        internal TableRef tabRef_;
        internal bool isOuterRef_;
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
                        context = parent;
                        isOuterRef_ = true;
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
            if (colName_.Equals(alias_))
                return $@"{para}{tabName_}.{colName_}[{ordinal_}]";
            return $@"{para}{tabName_}.{colName_} [{alias_}][{ordinal_}]";
        }
        public override Value Exec(Row input)
        {
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

        public override Value Exec(Row input)
        {
            switch (op_) {
                case "+": return l_.Exec(input) + r_.Exec(input);
                case "-": return l_.Exec(input) - r_.Exec(input);
                case "*": return l_.Exec(input) * r_.Exec(input);
                case "/": return l_.Exec(input) / r_.Exec(input);
                case ">": return l_.Exec(input) > r_.Exec(input) ? 1 : 0;
                case ">=": return l_.Exec(input) >= r_.Exec(input) ? 1 : 0;
                case "<": return l_.Exec(input) < r_.Exec(input) ? 1 : 0;
                case "<=": return l_.Exec(input) <= r_.Exec(input) ? 1 : 0;
                case "=": return l_.Exec(input) == r_.Exec(input)?1:0;
                default:
                    throw new NotImplementedException();
            }
        }
    }

    public class LogicAndExpr : BinExpr
    {
        public LogicAndExpr(Expr l, Expr r) : base(l, r, " and ") { }

        public override Value Exec(Row input)
        {
            Value lv = l_.Exec(input);
            Value rv = r_.Exec(input);

            if (lv == 1 && rv == 1)
                return 1;
            return 0;
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
        	// subquery id is global
            subqueryid_ = context.nSubqueries++;

            // we are using a new query context
            BindContext newcontext = new BindContext(context);
            query_.Bind(newcontext);

            // verify column count after bound because SelStar expansion
            if (query_.Selection().Count != 1)
                throw new SemanticAnalyzeException("subquery must return only one column");

            bounded_ = true;
        }


        public override Value Exec(Row input)
        {
            Row r = null;
            query_.GetPhysicPlan().Exec(l => {
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
        public SelectStmt query_;

        public CTExpr(string tabName, List<string> colNames, SelectStmt query) {
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
        public override Value Exec(Row input) => Value.Parse(val_.GetText());
    }

}
