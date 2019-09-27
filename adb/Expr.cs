using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections;
using System.Diagnostics;

namespace adb
{
    // it carries global info needed by expression binding
    public class BindContext {

        // Local section
        //      these fields are local to current subquery
        // -----------------------------

        // bounded tables
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

        public void AddTable(TableRef tab) {
            boundFrom_.Add(boundFrom_.Count, tab);
        }
    }

    static public class ExprHelper {
        static public Expr listToExpr(List<Expr> andlist)
        {
            Debug.Assert(andlist.Count >= 1);
            if (andlist.Count == 1)
                return andlist[0];
            else
            {
                var andexpr = new arithandexpr(andlist[0], andlist[1]);
                for (int i = 2; i < andlist.Count; i++)
                    andexpr.l_ = new arithandexpr(andexpr.l_, andlist[i]);
                return andexpr;
            }
        }
    }

    public class Expr
    {
        // an expression can reference multiple tables/columns
        //      e.g., a.i + b.j > [a.]k => references 2 tables and 3 columns
        // it is a sum of all its children
        //
        public BitArray whichTab_ = new BitArray(256);
        public BitArray whichCol_ = new BitArray(256);
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

        public virtual void Bind(BindContext context) { bounded_ = true; }
        public virtual string PrintString(int depth) { return ToString(); }
    }

    public class ColExpr : Expr
    {
        public string db_;
        public string table_;
        public string col_;

        public ColExpr(string db, string table, string col)
        {
            db_ = db; table_ = table; col_ = col;
        }

        public override void Bind(BindContext context)
        {
            // if table name is not given, get table by column name search
            if (table_ is null)
            {
                table_ = Catalog.systable_.ColumnFindTable(col_);
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
            }
            else
                throw new Exception($"can't find table {table_}");

            bounded_ = true;
        }

        public override string ToString()
        {
            return $@"{table_}.{col_}";
        }
    }

    public class FuncExpr : Expr {
        string func_;

        public FuncExpr(string func) {
            func_ = func;
        }
    }

    public class BinExpr : Expr
    {
        public Expr l_;
        public Expr r_;
        public int op_;

        public BinExpr(Expr l, Expr r, int op)
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
    }

    public class Arithplusexpr : BinExpr
    {
        public Arithplusexpr(Expr l, Expr r, int op) : base(l, r, op) { }

        public override string PrintString(int depth)
        {
            return l_.PrintString(depth) + '+' + r_.PrintString(depth);
        }
    }

    public class Arithtimesexpr : BinExpr
    {
        public Arithtimesexpr(Expr l, Expr r, int op) : base(l, r, op) { }
        public override string PrintString(int depth)
        {
            return l_.PrintString(depth) + '*' + r_.PrintString(depth);
        }
    }

    public class Arithcompexpr : BinExpr
    {
        public Arithcompexpr(Expr l, Expr r, int op) : base(l, r, op) { }
        public override string PrintString(int depth)
        {
            return l_.PrintString(depth) + '=' + r_.PrintString(depth);
        }
    }

    public class arithandexpr : BinExpr
    {
        public arithandexpr(Expr l, Expr r) : base(l, r, 0) { }
        public override string PrintString(int depth)
        {
            return l_.PrintString(depth) + " and " + r_.PrintString(depth);
        }
    }

    public class arithequalexpr : BinExpr
    {
        public arithequalexpr(Expr l, Expr r, int op) : base(l, r, op) { }

        public override string PrintString(int depth)
        {
            return l_.PrintString(depth) + '=' + r_.PrintString(depth);
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

        public override string PrintString(int depth)
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
    }

}
