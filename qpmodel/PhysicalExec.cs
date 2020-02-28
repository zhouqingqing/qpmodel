using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using qpmodel.expr;
using qpmodel.logic;

using Value = System.Object;

namespace qpmodel.physic
{
    public class SemanticExecutionException : Exception
    {
        public SemanticExecutionException(string msg) => Console.WriteLine($"ERROR[execution]: {msg}");
    }

    public class Row : IComparable
    {
        protected Value[] values_ = null;

        public Row(List<Value> values) => values_ = values.ToArray();

        // used by outer joins
        public Row(int length)
        {
            Debug.Assert(length >= 0);
            values_ = (Value[])Array.CreateInstance(typeof(Value), length);
            Array.ForEach(values_, x => Debug.Assert(x is null));
        }

        public Row(Row l, Row r)
        {
            // for semi/anti-semi joins, one of them may be null
            Debug.Assert(l != null || r != null);
            int size = l?.ColCount() ?? 0;
            size += r?.ColCount() ?? 0;
            values_ = (Value[])Array.CreateInstance(typeof(Value), size);

            int start = 0;
            if (l != null)
            {
                for (int i = 0; i < l.ColCount(); i++)
                    values_[start + i] = l[i];
                start += l.ColCount();
            }
            if (r != null)
            {
                for (int i = 0; i < r.ColCount(); i++)
                    values_[start + i] = r[i];
                start += r.ColCount();
            }
            Debug.Assert(start == size);
        }

        public Value this[int i]
        {
            get { return values_[i]; }
            set { values_[i] = value; }
        }
        public override int GetHashCode()
        {
            int hashcode = 0;
            Array.ForEach(values_, x => hashcode ^= x?.GetHashCode() ?? 0);
            return hashcode;
        }
        public override bool Equals(object obj)
        {
            var keyl = obj as Row;
            Debug.Assert(obj is Row);
            Debug.Assert(keyl.ColCount() == ColCount());
            return values_.SequenceEqual(keyl.values_);
        }

        public int CompareTo(object obj)
        {
            Debug.Assert(!(obj is null));
            var rrow = obj as Row;
            for (int i = 0; i < ColCount(); i++)
            {
                dynamic l = this[i];
                dynamic r = rrow[i];
                var c = l.CompareTo(r);
                if (c < 0)
                    return -1;
                else if (c == 0)
                    continue;
                else if (c > 0)
                    return 1;
            }
            return 0;
        }

        public int CompareTo(object obj, List<bool> descends)
        {
            Debug.Assert(!(obj is null));
            var rrow = obj as Row;

            Debug.Assert(descends.Count == ColCount());
            for (int i = 0; i < ColCount(); i++)
            {
                dynamic l = this[i];
                dynamic r = rrow[i];
                bool flip = descends[i];
                var c = l.CompareTo(r);
                if (c < 0)
                    return flip?+1:-1;
                else if (c == 0)
                    continue;
                else if (c > 0)
                    return flip?-1:+1;
            }
            return 0;
        }

        public int ColCount() => values_.Length;
        public override string ToString() => string.Join(",", values_.ToList());
    }

    public class Parameter
    {
        public readonly TableRef tabref_;   // from which table
        public readonly Row row_;   // what's the value of parameter

        public Parameter(TableRef tabref, Row row) { tabref_ = tabref; row_ = row; }
        public override string ToString() => $"?{tabref_}.{row_}";
    }

    public class ExecContext
    {
        public QueryOption option_;

        public bool stop_ = false;

        public List<Parameter> params_ = new List<Parameter>();

        public ExecContext(QueryOption option) { option_ = option; }

        public void Reset() { params_.Clear(); }
        public Value GetParam(TableRef tabref, int ordinal)
        {
        	Debug.Assert (!stop_);
            Debug.Assert(params_.FindAll(x => x.tabref_.Equals(tabref)).Count == 1);
            return params_.Find(x => x.tabref_.Equals(tabref)).row_[ordinal];
        }
        public void AddParam(TableRef tabref, Row row)
        {
        	Debug.Assert (!stop_);
            Debug.Assert(params_.FindAll(x => x.tabref_.Equals(tabref)).Count <= 1);
            params_.Remove(params_.Find(x => x.tabref_.Equals(tabref)));
            params_.Add(new Parameter(tabref, row));
        }
    }
}
