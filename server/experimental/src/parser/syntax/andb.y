/*
MIT License

Copyright (c) 2012-2017 Hasso-Plattner-Institut

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
/*
 * revised for andb 
 */
%{
/**
 * bison_parser.y
 * defines bison_parser.h
 * outputs bison_parser.cpp
 *
 * Grammar File Spec: http://dinosaur.compilertools.net/bison/bison_6.html
 *
 */
/*********************************
 ** Section 1: C Declarations
 *********************************/

#include <cstdio>
#include <cstring>

#include <stdint.h>

#include <locale>
#include <vector>
#include <iostream>
#include <sstream>
#include <new>

#include "common/common.h"
#include "common/dbcommon.h"
#include "parser/include/expr.h"
#include "parser/include/stmt.h"
#include "optimizer/binder.h"
#include "parser/include/SQLParserResult.h"
#include "parser/include/parser_typedef.h"


#include "andb_parser.h"
#include "andb_lexer.h"

using namespace andb;


int yyerror(YYLTYPE* llocp, SQLParserResult* result, yyscan_t scanner, const char *msg) {
	result->setIsValid(false);
	result->setErrorDetails(msg, llocp->first_line, llocp->first_column);
	return 0;
}

// selection, group by, order by etc.
void ReleaseMemory(std::vector<Expr*>* expr_vec)
{
   if (expr_vec) {
      for (int i = 0; i < expr_vec->size(); ++i) {
         Expr *e = expr_vec->at(i);
         delete e;
      }
      expr_vec->clear();
      delete expr_vec;
   }
}

// WHERE
void ReleaseMemory(Expr* expr)
{
    delete expr;
}

// FROM
void ReleaseMemory(std::vector<TableRef*>* table_vec)
{
    if (table_vec) {
        for (int i = 0; i < table_vec->size(); ++i) {
            TableRef *t = table_vec->at(i);
            delete t;
        }
        table_vec->clear();
        delete table_vec;
    }
}

// strings
void ReleaseMemory(std::vector<std::string*>* str_vec)
{
    if (str_vec) {
        for (int i = 0; i < str_vec->size(); ++i) {
            std::string *s = str_vec->at(i);
            delete s;
        }
        str_vec->clear();
        delete str_vec;
    }
}

%}
/*********************************
 ** Section 2: Bison Parser Declarations
 *********************************/


// Specify code that is included in the generated .h and .c files
%code requires {
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
}

// Define the names of the created files (defined in Makefile)
// %output  "bison_parser.cpp"
// %defines "bison_parser.h"

// Tell bison to create a reentrant parser
%define api.pure full

// Prefix the parser
%define api.prefix {andb_}
%define api.token.prefix {SQL_}

%define parse.error verbose
%locations

%initial-action {
	// Initialize
	@$.first_column = 0;
	@$.last_column = 0;
	@$.first_line = 0;
	@$.last_line = 0;
	@$.total_column = 0;
	@$.string_length = 0;
};


// Define additional parameters for yylex (http://www.gnu.org/software/bison/manual/html_node/Pure-Calling.html)
%lex-param   { yyscan_t scanner }

// Define additional parameters for yyparse
%parse-param { andb::SQLParserResult* result }
%parse-param { yyscan_t scanner }


/*********************************
 ** Define all data-types (http://www.gnu.org/software/bison/manual/html_node/Union-Decl.html)
 *********************************/
%union {
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
}


/*********************************
 ** Destructor symbols
 *********************************/
%destructor { delete $$; $$ = nullptr; } <sval>
%destructor {
   if (($$) != nullptr) {
      for (auto s : *($$)) {
         delete s;
      }
      delete $$;
      $$ = nullptr;
   }
} <str_vec> <table_vec> <column_vec> <expr_vec> <stmt_vec>


/*********************************
 ** Token Definition
 *********************************/
%token <sval> IDENTIFIER STRING
%token <fval> FLOATVAL
%token <ival> INTVAL

/* SQL Keywords */
%token DEALLOCATE PARAMETERS INTERSECT TEMPORARY TIMESTAMP
%token DISTINCT NVARCHAR RESTRICT TRUNCATE ANALYZE BETWEEN
%token CASCADE COLUMNS CONTROL DEFAULT EXECUTE EXPLAIN
%token INTEGER NATURAL PREPARE PRIMARY SCHEMAS
%token SPATIAL VARCHAR VIRTUAL DESCRIBE BEFORE COLUMN CREATE DELETE DIRECT
%token DOUBLE ESCAPE EXCEPT EXISTS EXTRACT CAST FORMAT GLOBAL HAVING IMPORT
%token INSERT ISNULL OFFSET RENAME SCHEMA SELECT SORTED
%token TABLES UNIQUE UNLOAD UPDATE VALUES AFTER ALTER CROSS
%token DELTA FLOAT GROUP INDEX INNER LIMIT LOCAL MERGE MINUS ORDER
%token OUTER RIGHT TABLE UNION USING WHERE CALL CASE CHAR COPY DATE DATETIME
%token DESC DROP ELSE FILE FROM FULL HASH HINT INTO JOIN
%token LEFT LIKE LOAD LONG NULL PLAN SHOW TEXT THEN TIME
%token VIEW WHEN WITH ADD ALL AND ASC END FOR INT KEY
%token NOT OFF SET TOP AS BY IF IN IS OF ON OR TO
%token ARRAY CONCAT ILIKE SECOND MINUTE HOUR DAY MONTH YEAR
%token TRUE FALSE
%token TRANSACTION BEGIN COMMIT ROLLBACK

/*********************************
 ** Non-Terminal types (http://www.gnu.org/software/bison/manual/html_node/Type-Decl.html)
 *********************************/
%type <stmt_vec>	    statement_list
%type <statement> 	    statement preparable_statement
%type <exec_stmt>	    execute_statement
%type <transaction_stmt>    transaction_statement
%type <prep_stmt>	    prepare_statement
%type <select_stmt>     select_statement select_with_paren select_no_paren select_clause select_within_set_operation select_within_set_operation_no_parentheses
%type <import_stmt>     import_statement
%type <export_stmt>     export_statement
%type <create_stmt>     create_statement
%type <insert_stmt>     insert_statement
%type <delete_stmt>     delete_statement truncate_statement
%type <update_stmt>     update_statement
%type <drop_stmt>	    drop_statement
%type <show_stmt>	    show_statement
%type <sval>            table_name
%type <sval> 		    file_path prepare_target_query
%type <bval> 		    opt_not_exists opt_exists opt_column_nullable opt_all
%type <uval>		    opt_join_type
%type <table_vec> 	 opt_from_clause from_clause table_list
%type <table>    table_ref table_ref_atomic table_ref_name nonjoin_table_ref_atomic
%type <table>		    join_clause table_ref_name_no_alias
%type <expr> 		    expr operand scalar_expr unary_expr binary_expr logic_expr exists_expr extract_expr cast_expr
%type <expr>		    function_expr between_expr expr_alias param_expr
%type <expr> 		    column_name literal int_literal num_literal string_literal bool_literal
%type <expr> 		    comp_expr opt_where join_condition opt_having case_expr case_list in_expr hint
%type <expr> 		    array_expr array_index null_literal
%type <limit>		    opt_limit opt_top
%type <order>		    order_desc
%type <order_type>	    opt_order_type
%type <datetime_field>	datetime_field
%type <column_t>	    column_def
%type <column_type_t>   column_type
%type <update_t>	    update_clause
%type <group_t>		    opt_group
%type <sval>		    opt_table_alias table_alias opt_alias alias
%type <with_description_t>  with_description
%type <set_operator_t>  set_operator set_type

// ImportType is used for compatibility reasons
%type <import_type_t>	opt_file_type file_type

%type <str_vec>			ident_commalist opt_column_list
%type <expr_vec> 		expr_list select_list opt_literal_list literal_list hint_list opt_hints
%type <table_vec> 		table_ref_commalist
%type <order_vec>		opt_order order_list
%type <with_description_vec> 	opt_with_clause with_clause with_description_list
%type <update_vec>		update_clause_commalist
%type <column_vec>		column_def_commalist

/******************************
 ** Token Precedence and Associativity
 ** Precedence: lowest to highest
 ******************************/
%left		OR
%left		AND
%right		NOT
%nonassoc	'=' EQUALS NOTEQUALS LIKE ILIKE
%nonassoc	'<' '>' LESS GREATER LESSEQ GREATEREQ

%nonassoc	NOTNULL
%nonassoc	ISNULL
%nonassoc	IS				/* sets precedence for IS NULL, etc */
%left		'+' '-'
%left		'*' '/' '%'
%left		'^'
%left		CONCAT

/* Unary Operators */
%right  UMINUS
%left		'[' ']'
%left		'(' ')'
%left		'.'
%left   JOIN
%%
/*********************************
 ** Section 3: Grammar Definition
 *********************************/

// Defines our general input.
input:
		statement_list ';' {
			for (SQLStatement* stmt : *$1) {
				// Transfers ownership of the statement.
				result->addStatement(stmt);
			}

			unsigned param_id = 0;
			for (void* param : yyloc.param_list) {
				if (param != nullptr) {
					Expr* expr = (Expr*) param;
					expr->ival = param_id;
					result->addParameter(expr);
					++param_id;
				}
			}
			delete $1;
		}
	;


statement_list:
		statement {
         /* ACCEPT: statement */
			$1->stringLength = yylloc.string_length;
			yylloc.string_length = 0;
			$$ = new std::vector<SQLStatement*>();
#ifdef __DEBUG_PARSER_MEMLEAK
         std::cout << __FILE__ << " " << __LINE__ << " PAR STMTVEC PTR " << (void*)$$ << std::endl;
#endif
			$$->push_back($1);
		}
	|	statement_list ';' statement {
         /* ADD: statement to statement_list */
			$3->stringLength = yylloc.string_length;
			yylloc.string_length = 0;
			$1->push_back($3);
			$$ = $1;
		}
	;

statement:
		select_statement { $$ = $1; }
	;


/******************************
 * Select Statement
 ******************************/

select_statement:
		SELECT select_list from_clause opt_where {
         /* ACCEPT: select_statement */
			$$ = new SelectStatement();
#ifdef __DEBUG_PARSER_MEMLEAK
         std::cout << __FILE__ << " " << __LINE__ << " PAR SELETMT PTR " << (void*)$$ << std::endl;
#endif
         $$->setSelections($2);
         $$->setFrom($3);
         $$->where_     = $4;

    /*
    * Something is not right here, all of these objects were cloned
    * into respective places in the SQLStatement but somehow
    * these deletes are deleting the clones too.
         ReleaseMemory($2);
         ReleaseMemory($3);
         ReleaseMemory($4);

         $2 = nullptr;
         $3 = nullptr;
         $4 = nullptr;
    */
		}
	;

opt_where:
        WHERE expr {
         /* ACCEPT: opt_where */
           $$ = $2;
        }

    | {$$ = nullptr;} /* empty */
    ;

select_list:
		expr_list
	;


from_clause:
		FROM table_list {
         /* ACCEPT: from_clause */
         $$ = $2;
      }
	;


/******************************
 * Expressions
 ******************************/
expr_list:
		expr_alias {
         /* ACCEPT: expr_alias */
         $$ = new std::vector<Expr*>();
#ifdef __DEBUG_PARSER_MEMLEAK
         std::cout << __FILE__ << " " << __LINE__ << " PAR EXPRVEC PTR " << (void*)$$ << std::endl;
#endif
         $$->push_back($1);
      }
	|	expr_list ',' expr_alias {
         /* ADD: expr_alias to expr_list */
         $1->push_back($3);
         $$ = $1;
      }
	;

expr_alias:
		expr opt_alias {
         /* ACCEPT: expr opt_alias */
			$$ = $1;
			if ($2) {
				$$->alias_ = $2;
			}
      }
	;

expr:
		operand
   | logic_expr
	;

operand:
      '(' expr ')' { $$ = $2; }
	|	scalar_expr
	|	binary_expr
	;

scalar_expr:
		column_name
	|	literal
	;


binary_expr:
		comp_expr
	|	operand '-' operand			{
         Expr *left = $1->Clone();
         Expr *right = $3->Clone();
         $$ = makeOpBinary(left, BinOp::Sub, right);
      }
	|	operand '+' operand			{
         Expr *left = $1->Clone();
         Expr *right = $3->Clone();
         $$ = makeOpBinary(left, BinOp::Add, right);
      }
	|	operand '/' operand			{
         Expr *left = $1->Clone();
         Expr *right = $3->Clone();
         $$ = makeOpBinary(left, BinOp::Div, right);
      }
	|	operand '*' operand			{
         Expr *left = $1->Clone();
         Expr *right = $3->Clone();
         $$ = makeOpBinary(left, BinOp::Mul, right);
      }
	;

logic_expr:
          expr AND expr {
            Expr *left = $1->Clone();
            Expr *right = $3->Clone();
            $$ = makeOpBinary(left, BinOp::And, right);
        }
      | expr OR expr {
             Expr *left = $1->Clone();
             Expr *right = $3->Clone();
              $$ = makeOpBinary(left, BinOp::Or, right);
          }
      ;

comp_expr:
		operand '=' operand			{
             Expr *left = $1->Clone();
             Expr *right = $3->Clone();
            $$ = makeOpBinary(left, BinOp::Equal, right);
        }
	|	operand EQUALS operand			{
             Expr *left = $1->Clone();
             Expr *right = $3->Clone();
            $$ = makeOpBinary(left, BinOp::Equal, right);
        }
	|	operand NOTEQUALS operand	{
             Expr *left = $1->Clone();
             Expr *right = $3->Clone();
            $$ = makeOpBinary(left, BinOp::Neq, right);
        }
	|	operand '<' operand			{
             Expr *left = $1->Clone();
             Expr *right = $3->Clone();
            $$ = makeOpBinary(left, BinOp::Less, right);
        }
	|	operand '>' operand			{
             Expr *left = $1->Clone();
             Expr *right = $3->Clone();
            $$ = makeOpBinary(left, BinOp::Great, right);
        }
	|	operand LESSEQ operand		{
             Expr *left = $1->Clone();
             Expr *right = $3->Clone();
            $$ = makeOpBinary(left, BinOp::Leq, right);
        }
	|	operand GREATEREQ operand	{
             Expr *left = $1->Clone();
             Expr *right = $3->Clone();
            $$ = makeOpBinary(left, BinOp::Geq, right);
        }
	;

column_name:
		IDENTIFIER {
            $$ = makeColumnRef($1);
            delete $1;
        }
	|	IDENTIFIER '.' IDENTIFIER {
            $$ = makeColumnRef($3, $1);
            delete $1;
            delete $3;
        }
	|	'*' { $$ = makeStar(); }
	|	IDENTIFIER '.' '*' {
            $$ = makeStar($1);
            delete $1;
        }
	;

literal:
		string_literal
	|	bool_literal
	|	num_literal
	|	null_literal
	;

string_literal:
		STRING { $$ = makeLiteral($1); }
	;

bool_literal:
		TRUE { $$ = makeLiteral(true); }
	|	FALSE { $$ = makeLiteral(false); }
	;

num_literal:
		FLOATVAL { $$ = makeLiteral($1); }
	|	int_literal
	;

int_literal:
		INTVAL { $$ = makeLiteral($1); }
	;

null_literal:
		NULL { $$ = makeNullLiteral(); }
	;


/******************************
 * Table
 ******************************/
table_list:
      table_ref {
         /* ACCEPT: table_ref */
          $$ = new std::vector<TableRef *>();
#ifdef __DEBUG_PARSER_MEMLEAK
         std::cout << __FILE__ << " " << __LINE__ << " PAR TABREFVEC PTR " << (void*)$$ << std::endl;
#endif
          $$->push_back($1);
       }
      | table_list ',' table_ref {
            /* ADD: table_ref to table_list */
            $1->push_back($3);
            $$ = $1;
         }
	;


table_ref:
      table_name {
         $$ = new BaseTableRef($1, nullptr);
#ifdef __DEBUG_PARSER_MEMLEAK
         std::cout << __FILE__ << " " << __LINE__ << " PAR BASETAB NAME " << $1 << " PTR " << (void*)$$ << std::endl;
#endif
            delete $1;
         }
      | table_name alias {
			$$ = new BaseTableRef($1, $2);
#ifdef __DEBUG_PARSER_MEMLEAK
         std::cout << __FILE__ << " " << __LINE__ << " PAR BASETAB NAME " << $1 << " ALIAS " << $2 << " PTR " << (void*)$$ << std::endl;
#endif
            delete $1;
            delete $2;
		}
	;

table_name:
		IDENTIFIER {
         /* ACCEPT: table_name */
         $$ = $1;
      }
	;


opt_alias:
		alias
	|	/* empty */ { $$ = nullptr; }
	;

alias:
		AS IDENTIFIER {
         /* ACCEPT: AS alias */
         $$ = $2;
      }
	|	IDENTIFIER {
         /* ACCEPT: alias */
         $$ = $1;
      }
	;

/******************************
 * Join Statements
 ******************************/

/******************************
 * Misc
 ******************************/

ident_commalist:
		IDENTIFIER {
         $$ = new std::vector<std::string*>();
#ifdef __DEBUG_PARSER_MEMLEAK
         std::cout << __FILE__ << " " << __LINE__ << " PAR IDVEC PTR " << (void*)$$ << std::endl;
#endif
         $$->push_back($1);
      }
	|	ident_commalist ',' IDENTIFIER {
         $1->push_back($3);
         $$ = $1;
      }
	;

%%
/*********************************
 ** Section 4: Additional C code
 *********************************/

/* empty */
