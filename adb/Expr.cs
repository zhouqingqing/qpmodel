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
        Dictionary<int, TableRef> boundFrom_ = new Dictionary<int, TableRef>();
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

        public int TableIndex(string name) {
            foreach (var e in boundFrom_) {
                if (e.Value.alias_.Equals(name))
                    return e.Key;
            }

            return -1;
        }

        public TableRef Table(string name)
        {
            foreach (var e in boundFrom_)
            {
                if (e.Value.alias_.Equals(name))
                    return e.Value;
            }

            return null;
        }

        public void AddTable(TableRef tab) {
            boundFrom_.Add(boundFrom_.Count, tab);
        }

        public List<TableRef> TableList() => boundFrom_.Values.ToList();
    }

    static public class ExprHelper {
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
    }

    public class Expr
    {
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
                }
                return false;
            }
            return true;
        }

        public bool HasSubQuery() {
            return VisitEachExpr(e => e is SubqueryExpr);
        }

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
        public virtual void Bind(BindContext context) { bounded_ = true; }
        public virtual string PrintString(int depth) { return ToString(); }
        public virtual Value Exec(Row input) { return Value.MaxValue;}
    }

    // represents "*" or "table.*"
    public class SelStar : Expr {
        internal string table_;

        // bound
        List<Expr> exprs_;

        public SelStar(string table) => table_ = table;

        public override void Bind(BindContext context)
        {
            exprs_ = new List<Expr>();
            if (table_ is null)
            {
                // *
                context.TableList().ForEach(x 
                    => exprs_.AddRange(x.GenerateAllColumnsRefs()));
            }
            else {
                // table.*
                exprs_.AddRange(context.Table(table_).GenerateAllColumnsRefs());
            }
        }

        public override string ToString()
        {
            if (exprs_ is null)
                // if not bound
                return table_ + ".*";
            else
                // after bound
                return string.Join(",", exprs_);
        }
    }

    public class ColExpr : Expr
    {
        internal string db_;
        internal string table_;
        internal string col_;

        // -- execution section --

        // bound: which column in the input row
        internal int ordinal_;

        public ColExpr(string db, string table, string col)
        {
            db_ = db; table_ = table; col_ = col;
        }

        public override void Bind(BindContext context)
        {
            // if table name is not given, get table by column name search
            if (table_ is null)
            {
                table_ = Catalog.systable_.ColumnFindTable(col_).name_;
                if (table_ is null)
                    throw new Exception($@"can't find column {col_}");
            }

            // mark column's tableref
            int tableindex = context.TableIndex(table_);
            if (tableindex == -1)
            {
                // can't find in my current context, try my ancestors
                BindContext parent = context;
                while ((parent = parent.parent_) != null)
                {
                    tableindex = parent.TableIndex(table_);
                    if (tableindex != -1)
                        break;
                }
            }
            else
            {
                // current context solvable
                whichTab_.Set(context.TableIndex(table_), true);
            }

            if (tableindex != -1)
            {
                ordinal_ = Catalog.systable_.Column(table_, col_).ordinal_;
            }
            else
                throw new Exception($"can't find table {table_}");

            bounded_ = true;
        }
        public override string ToString()
        {
            return $@"{table_}.{col_}";
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
            Value r = Value.MaxValue;
            switch (op_) {
                case "+":
                    r = l_.Exec(input) + r_.Exec(input);
                    break;
                case "*":
                    r = l_.Exec(input) * r_.Exec(input);
                    break;
                case ">":
                    r = l_.Exec(input) > r_.Exec(input) ? 1 : 0;
                    break;
                case "=":
                    r = l_.Exec(input) == r_.Exec(input)?1:0;
                    break;
                default:
                    throw new NotImplementedException();
            }

            return r;
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
        public SelectCore query_;
        public int subqueryid_; // bound

        // bounded data

        public SubqueryExpr(SelectCore query) { query_ = query; }

        public override void Bind(BindContext context)
        {
        	// subquery id is global
            subqueryid_ = context.nSubqueries++;

            // we are using a new query context
            BindContext newcontext = new BindContext(context);
            query_.Bind(newcontext);
            bounded_ = true;
        }

        public override string ToString()
        {
            return $@"@{subqueryid_}";
        }
    }

    public class LiteralExpr : Expr
    {
        public SQLiteParser.Literal_valueContext val_;

        public LiteralExpr(SQLiteParser.Literal_valueContext val)
        {
            val_ = val;
        }

        public override string ToString()
        {
            return val_.GetText();
        }

        public override Value Exec(Row input)
        {
            return Value.Parse(val_.GetText());
        }
    }

}
