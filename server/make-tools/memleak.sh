#! /bin/sh

[ $# = 1 ] || {
   echo "Usage: $0 infile"
   exit 1
}

INFILE=$1


TMPFILE="$0_$$.tmp"
trap "rm -f $TMPFILE > /dev/null 2>&1" 0 1 2 3 4 5 6 7 8 10 11 12 13 14 15

# 1                                             2                                        3         4                      5
# C:\Users\pkommoju\Documents\GitHub\qpmodel\server\experimental\src\common/dbcommon.h: 155: ColumnDef_cons(0) : PTR 00000006F90FF588
# C:\Users\pkommoju\Documents\GitHub\qpmodel\server\experimental\src\common/dbcommon.h: 161: ColumnDef_dest(0) : PTR 00000006F90FF588
awk -F: '
BEGIN {
CLS=""
TYP=""
PTR=""
NAL=0
NDL=0
}

function extract_details(carg, parg) {
   split(carg, ctag, "_");
   class=ctag[1];
   extra=index(class, "(");
   if (extra > 0)
      CLS=substr(class, 1, extra - 1);
   else
      CLS=class
   split(parg, ptag, " ");
   PTR=ptag[2];
   reurn 0;
}

/_cons/ {
   extract_details($4, $5);
   printf "CONS %s %s\n", CLS, PTR;
   PHASH[PTR]++;
   CHASH[CLS]++
   ++NAL;
}

/_dest/ {
   extract_details($4, $5);
   printf "DEST %s %s\n", CLS, PTR;
   PHASH[PTR]--
   CHASH[CLS]--
   ++NDL
}

END {
   for (h in PHASH) {
      printf("%s %d\n", h, PHASH[h]);
   }

   printf("%-30s % 11s\n", "Class", "Unfreed");
   for (c in CHASH) {
      printf("%-30s % 11d\n", c, CHASH[c])
   }

   printf("NAL %d NDL %d LEAKS %d\n", NAL, NDL, NAL - NDL);
}
' $INFILE
cat $TMPFILE
