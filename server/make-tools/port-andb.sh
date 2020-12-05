#! /bin/sh

NOL=0
NOY=0
[ -f andb.l ] || {
   NOL=1
}

[ -f andb.y ] || {
   NOY=1
}

if [ $NOL -eq 1 -a $NOY -eq 1 ]; then
    "No .l and no .y"
    exit 1
fi

for f in andb.l andb.y; do
    if [ ! -f $f ]; then
        continue
    fi
    sed -e '
s/bison_parser.h/andb_parser.h/g
s/bison_parser.c/andb_parser.c/g
s/flex_lexer.h/andb_lexer.h/g
s/flex_lexer.cpp/andb_lexer.cpp/g
s/#.*include.*unistd.h.*/\
#ifndef _MSC_VER\
#include <unistd.h>\
#endif\
/
s/"flex_lexer.h"/"andb_lexer.h"/
s/"flex_lexer.cpp"/"andb_lexer.cpp"/
s:^[\t ]*//.*%output:%output:
s:^[\t ]*//.*%defines:%defines:
' $f > $f.new
done

if [ $NOL -eq 0 ]; then
    mv andb.l.new andb_lexer.l || {
        echo "rename of x.new to x failed"
        exit 1
    }
fi

if [ $NOY -eq 0 ]; then
    mv andb.y.new andb_parser.y || {
        echo "rename of x.new to x failed"
        exit 1
    }
fi

for f in andb_*.{c,h,y,l,cpp}; do
    if [ ! -f $f ]; then
        continue
    fi
   awk '
   BEGIN {notif=1}
/^#endif \/\/ __LATER/ {notif=1; next}
/^#ifdef __LATER/ {notif=0; next}
/^#else/    {notif=1; next}
{if (notif) { print} else {next}}
' $f > $f.tmp

if [ $? -ne 0 ]; then
   echo "failed to remove #ifdef __LATER"
   exit 1
else
   mv $f.tmp $f
   if [ $? -ne 0 ]; then
      echo "failed to restore $f"
      exit 1
   fi
fi
done

if [ $NOY -eq 0 ]; then
    bison -l --verbose andb_parser.y || {
        echo "bison failed"
        exit 1
    }
fi

if [ $NOL -eq 0 ]; then
    flex -L andb_lexer.l || {
        echo "flex failed"
        exit 1
    }
fi

for f in *.h *.cpp; do
   sed -i '
s/#.*include.*unistd\.h.*/\
#ifndef _MSC_VER\
#include <unistd.h>\
#endif\
/
/#.*line /d
s/^[\t ]*register[\t ][\t ]*//
' $f
done

for f in *.[h,l,y] *.cpp; do
    dos2unix $f
done

# add using namespace to header files.
if [ -f andb_lexer.h ]; then
    sed -i '
/#ifndef FLEXINT_H/i\
using namespace andb;\
\
' andb_lexer.h
fi

if [ -f andb_parser.h ]; then
    sed -i '
/#ifndef YY_ANDB_ANDB_PARSER_H_INCLUDED/i\
using namespace andb;\
\
' andb_parser.h
fi

exit 0
