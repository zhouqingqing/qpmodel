/* A Bison parser, made by GNU Bison 3.7.  */

/* Bison interface for Yacc-like parsers in C

   Copyright (C) 1984, 1989-1990, 2000-2015, 2018-2020 Free Software Foundation,
   Inc.

   This program is free software: you can redistribute it and/or modify
   it under the terms of the GNU General Public License as published by
   the Free Software Foundation, either version 3 of the License, or
   (at your option) any later version.

   This program is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
   GNU General Public License for more details.

   You should have received a copy of the GNU General Public License
   along with this program.  If not, see <http://www.gnu.org/licenses/>.  */

/* As a special exception, you may create a larger work that contains
   part or all of the Bison parser skeleton and distribute that work
   under terms of your choice, so long as that work isn't itself a
   parser generator using the skeleton or a modified version thereof
   as a parser skeleton.  Alternatively, if you modify or redistribute
   the parser skeleton itself, you may (at your option) remove this
   special exception, which will cause the skeleton and the resulting
   Bison output files to be licensed under the GNU General Public
   License without this special exception.

   This special exception was added by the Free Software Foundation in
   version 2.2 of Bison.  */

/* DO NOT RELY ON FEATURES THAT ARE NOT DOCUMENTED in the manual,
   especially those whose name start with YY_ or yy_.  They are
   private implementation details that can be changed or removed.  */

using namespace andb;


#ifndef YY_ANDB_ANDB_PARSER_H_INCLUDED
# define YY_ANDB_ANDB_PARSER_H_INCLUDED
/* Debug traces.  */
#ifndef ANDB_DEBUG
# if defined YYDEBUG
#if YYDEBUG
#   define ANDB_DEBUG 1
#  else
#   define ANDB_DEBUG 0
#  endif
# else /* ! defined YYDEBUG */
#  define ANDB_DEBUG 0
# endif /* ! defined YYDEBUG */
#endif  /* ! defined ANDB_DEBUG */
#if ANDB_DEBUG
extern int andb_debug;
#endif
/* "%code requires" blocks.  */

// %code requires block


// Auto update column and line number
#define YY_USER_ACTION \
		yylloc->first_line = yylloc->last_line; \
		yylloc->first_column = yylloc->last_column; \
		for(int i = 0; yytext[i] != '\0'; i++) { \
			yylloc->total_column++; \
			yylloc->string_length++; \
				if(yytext[i] == '\n') { \
						yylloc->last_line++; \
						yylloc->last_column = 0; \
				} \
				else { \
						yylloc->last_column++; \
				} \
		}


/* Token kinds.  */
#ifndef ANDB_TOKENTYPE
# define ANDB_TOKENTYPE
  enum andb_tokentype
  {
    SQL_ANDB_EMPTY = -2,
    SQL_YYEOF = 0,                 /* "end of file"  */
    SQL_ANDB_error = 256,          /* error  */
    SQL_ANDB_UNDEF = 257,          /* "invalid token"  */
    SQL_IDENTIFIER = 258,          /* IDENTIFIER  */
    SQL_STRING = 259,              /* STRING  */
    SQL_FLOATVAL = 260,            /* FLOATVAL  */
    SQL_INTVAL = 261,              /* INTVAL  */
    SQL_DEALLOCATE = 262,          /* DEALLOCATE  */
    SQL_PARAMETERS = 263,          /* PARAMETERS  */
    SQL_INTERSECT = 264,           /* INTERSECT  */
    SQL_TEMPORARY = 265,           /* TEMPORARY  */
    SQL_TIMESTAMP = 266,           /* TIMESTAMP  */
    SQL_DISTINCT = 267,            /* DISTINCT  */
    SQL_NVARCHAR = 268,            /* NVARCHAR  */
    SQL_RESTRICT = 269,            /* RESTRICT  */
    SQL_TRUNCATE = 270,            /* TRUNCATE  */
    SQL_ANALYZE = 271,             /* ANALYZE  */
    SQL_BETWEEN = 272,             /* BETWEEN  */
    SQL_CASCADE = 273,             /* CASCADE  */
    SQL_COLUMNS = 274,             /* COLUMNS  */
    SQL_CONTROL = 275,             /* CONTROL  */
    SQL_DEFAULT = 276,             /* DEFAULT  */
    SQL_EXECUTE = 277,             /* EXECUTE  */
    SQL_EXPLAIN = 278,             /* EXPLAIN  */
    SQL_INTEGER = 279,             /* INTEGER  */
    SQL_NATURAL = 280,             /* NATURAL  */
    SQL_PREPARE = 281,             /* PREPARE  */
    SQL_PRIMARY = 282,             /* PRIMARY  */
    SQL_SCHEMAS = 283,             /* SCHEMAS  */
    SQL_SPATIAL = 284,             /* SPATIAL  */
    SQL_VARCHAR = 285,             /* VARCHAR  */
    SQL_VIRTUAL = 286,             /* VIRTUAL  */
    SQL_DESCRIBE = 287,            /* DESCRIBE  */
    SQL_BEFORE = 288,              /* BEFORE  */
    SQL_COLUMN = 289,              /* COLUMN  */
    SQL_CREATE = 290,              /* CREATE  */
    SQL_DELETE = 291,              /* DELETE  */
    SQL_DIRECT = 292,              /* DIRECT  */
    SQL_DOUBLE = 293,              /* DOUBLE  */
    SQL_ESCAPE = 294,              /* ESCAPE  */
    SQL_EXCEPT = 295,              /* EXCEPT  */
    SQL_EXISTS = 296,              /* EXISTS  */
    SQL_EXTRACT = 297,             /* EXTRACT  */
    SQL_CAST = 298,                /* CAST  */
    SQL_FORMAT = 299,              /* FORMAT  */
    SQL_GLOBAL = 300,              /* GLOBAL  */
    SQL_HAVING = 301,              /* HAVING  */
    SQL_IMPORT = 302,              /* IMPORT  */
    SQL_INSERT = 303,              /* INSERT  */
    SQL_ISNULL = 304,              /* ISNULL  */
    SQL_OFFSET = 305,              /* OFFSET  */
    SQL_RENAME = 306,              /* RENAME  */
    SQL_SCHEMA = 307,              /* SCHEMA  */
    SQL_SELECT = 308,              /* SELECT  */
    SQL_SORTED = 309,              /* SORTED  */
    SQL_TABLES = 310,              /* TABLES  */
    SQL_UNIQUE = 311,              /* UNIQUE  */
    SQL_UNLOAD = 312,              /* UNLOAD  */
    SQL_UPDATE = 313,              /* UPDATE  */
    SQL_VALUES = 314,              /* VALUES  */
    SQL_AFTER = 315,               /* AFTER  */
    SQL_ALTER = 316,               /* ALTER  */
    SQL_CROSS = 317,               /* CROSS  */
    SQL_DELTA = 318,               /* DELTA  */
    SQL_FLOAT = 319,               /* FLOAT  */
    SQL_GROUP = 320,               /* GROUP  */
    SQL_INDEX = 321,               /* INDEX  */
    SQL_INNER = 322,               /* INNER  */
    SQL_LIMIT = 323,               /* LIMIT  */
    SQL_LOCAL = 324,               /* LOCAL  */
    SQL_MERGE = 325,               /* MERGE  */
    SQL_MINUS = 326,               /* MINUS  */
    SQL_ORDER = 327,               /* ORDER  */
    SQL_OUTER = 328,               /* OUTER  */
    SQL_RIGHT = 329,               /* RIGHT  */
    SQL_TABLE = 330,               /* TABLE  */
    SQL_UNION = 331,               /* UNION  */
    SQL_USING = 332,               /* USING  */
    SQL_WHERE = 333,               /* WHERE  */
    SQL_CALL = 334,                /* CALL  */
    SQL_CASE = 335,                /* CASE  */
    SQL_CHAR = 336,                /* CHAR  */
    SQL_COPY = 337,                /* COPY  */
    SQL_DATE = 338,                /* DATE  */
    SQL_DATETIME = 339,            /* DATETIME  */
    SQL_DESC = 340,                /* DESC  */
    SQL_DROP = 341,                /* DROP  */
    SQL_ELSE = 342,                /* ELSE  */
    SQL_FILE = 343,                /* FILE  */
    SQL_FROM = 344,                /* FROM  */
    SQL_FULL = 345,                /* FULL  */
    SQL_HASH = 346,                /* HASH  */
    SQL_HINT = 347,                /* HINT  */
    SQL_INTO = 348,                /* INTO  */
    SQL_JOIN = 349,                /* JOIN  */
    SQL_LEFT = 350,                /* LEFT  */
    SQL_LIKE = 351,                /* LIKE  */
    SQL_LOAD = 352,                /* LOAD  */
    SQL_LONG = 353,                /* LONG  */
    SQL_NULL = 354,                /* NULL  */
    SQL_PLAN = 355,                /* PLAN  */
    SQL_SHOW = 356,                /* SHOW  */
    SQL_TEXT = 357,                /* TEXT  */
    SQL_THEN = 358,                /* THEN  */
    SQL_TIME = 359,                /* TIME  */
    SQL_VIEW = 360,                /* VIEW  */
    SQL_WHEN = 361,                /* WHEN  */
    SQL_WITH = 362,                /* WITH  */
    SQL_ADD = 363,                 /* ADD  */
    SQL_ALL = 364,                 /* ALL  */
    SQL_AND = 365,                 /* AND  */
    SQL_ASC = 366,                 /* ASC  */
    SQL_END = 367,                 /* END  */
    SQL_FOR = 368,                 /* FOR  */
    SQL_INT = 369,                 /* INT  */
    SQL_KEY = 370,                 /* KEY  */
    SQL_NOT = 371,                 /* NOT  */
    SQL_OFF = 372,                 /* OFF  */
    SQL_SET = 373,                 /* SET  */
    SQL_TOP = 374,                 /* TOP  */
    SQL_AS = 375,                  /* AS  */
    SQL_BY = 376,                  /* BY  */
    SQL_IF = 377,                  /* IF  */
    SQL_IN = 378,                  /* IN  */
    SQL_IS = 379,                  /* IS  */
    SQL_OF = 380,                  /* OF  */
    SQL_ON = 381,                  /* ON  */
    SQL_OR = 382,                  /* OR  */
    SQL_TO = 383,                  /* TO  */
    SQL_ARRAY = 384,               /* ARRAY  */
    SQL_CONCAT = 385,              /* CONCAT  */
    SQL_ILIKE = 386,               /* ILIKE  */
    SQL_SECOND = 387,              /* SECOND  */
    SQL_MINUTE = 388,              /* MINUTE  */
    SQL_HOUR = 389,                /* HOUR  */
    SQL_DAY = 390,                 /* DAY  */
    SQL_MONTH = 391,               /* MONTH  */
    SQL_YEAR = 392,                /* YEAR  */
    SQL_TRUE = 393,                /* TRUE  */
    SQL_FALSE = 394,               /* FALSE  */
    SQL_TRANSACTION = 395,         /* TRANSACTION  */
    SQL_BEGIN = 396,               /* BEGIN  */
    SQL_COMMIT = 397,              /* COMMIT  */
    SQL_ROLLBACK = 398,            /* ROLLBACK  */
    SQL_EQUALS = 399,              /* EQUALS  */
    SQL_NOTEQUALS = 400,           /* NOTEQUALS  */
    SQL_LESS = 401,                /* LESS  */
    SQL_GREATER = 402,             /* GREATER  */
    SQL_LESSEQ = 403,              /* LESSEQ  */
    SQL_GREATEREQ = 404,           /* GREATEREQ  */
    SQL_NOTNULL = 405,             /* NOTNULL  */
    SQL_UMINUS = 406               /* UMINUS  */
  };
  typedef enum andb_tokentype andb_token_kind_t;
#endif

/* Value type.  */
#if ! defined ANDB_STYPE && ! defined ANDB_STYPE_IS_DECLARED
union ANDB_STYPE
{

	double      fval;
	int64_t     ival;
   std::string *sval;
	uintmax_t   uval;
	bool        bval;

	andb::SQLStatement      *statement;
	andb::SelectStatement   *select_stmt;

	andb::TableRef  *table;
	andb::Expr      *expr;

	andb::ColumnDef   *column_t;
	andb::ColumnType  column_type_t;
	std::vector<andb::SQLStatement*>    *stmt_vec;

   std::vector<Expr*>       *expr_vec;  // selection, group by, order by etc.
   std::vector<TableRef*>   *table_vec; // FROM
	std::vector<std::string*>     *str_vec;

   // need to provide them because they get "deleted" by the compiler
   ANDB_STYPE() { ival = 0; } 
   ~ANDB_STYPE() {}

   ANDB_STYPE(const ANDB_STYPE &oth) { ival = oth.ival; }

   ANDB_STYPE& operator=(const ANDB_STYPE &oth) {
      ival = oth.ival;

      return *this;
   }


};
typedef union ANDB_STYPE ANDB_STYPE;
# define ANDB_STYPE_IS_TRIVIAL 1
# define ANDB_STYPE_IS_DECLARED 1
#endif

/* Location type.  */
#if ! defined ANDB_LTYPE && ! defined ANDB_LTYPE_IS_DECLARED
typedef struct ANDB_LTYPE ANDB_LTYPE;
struct ANDB_LTYPE
{
  int first_line;
  int first_column;
  int last_line;
  int last_column;
};
# define ANDB_LTYPE_IS_DECLARED 1
# define ANDB_LTYPE_IS_TRIVIAL 1
#endif



int andb_parse (andb::SQLParserResult* result, yyscan_t scanner);

#endif /* !YY_ANDB_ANDB_PARSER_H_INCLUDED  */
