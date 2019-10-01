using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adb
{
    static public class Utils
    {
        // a contains b?
        public static bool ListAContainsB<T>(List<T> a, List<T> b) => !b.Except(a).Any();
    }
}
