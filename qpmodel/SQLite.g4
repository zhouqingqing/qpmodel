 /*
 * The MIT License (MIT)
 *
 * Copyright (c) 2014 by Bart Kiers
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * Project      : sqlite-parser; an ANTLR4 grammar for SQLite
 *                https://github.com/bkiers/sqlite-parser
 * Developed by : Bart Kiers, bart@big-o.nl
 */
 
 /*
  * Further simplified and modified by moi
  */
grammar SQLite;

parse
 : ( sql_stmt_list | error )* EOF
 ;

error
 : UNEXPECTED_CHAR 
   { 
     throw new AntlrParserException("UNEXPECTED_CHAR=" + $UNEXPECTED_CHAR.text); 
   }
 ;

sql_stmt_list
 : ';'* sql_stmt ( ';'+ sql_stmt )* ';'*
 ;

sql_stmt
 : ( K_EXPLAIN ( K_EXECUTE | K_FULL )? ( K_VERBOSE )? )? (
                                      analyze_stmt
                                      | create_index_stmt
                                      | create_table_stmt
                                      | delete_stmt
                                      | drop_index_stmt
                                      | drop_table_stmt
                                      | insert_stmt
                                      | copy_stmt
                                      | set_stmt
                                      | select_stmt
                                      | update_stmt
                                      )
 ;

analyze_stmt
 : K_ANALYZE table_name? (tablesample_clause)*
 ;

create_index_stmt
 : K_CREATE K_UNIQUE? K_INDEX ( K_IF K_NOT K_EXISTS )?
   ( database_name '.' )? index_name K_ON table_name '(' indexed_column ( ',' indexed_column )* ')'
   ( K_WHERE expr )?
 ;

create_table_stmt
 : K_CREATE K_TABLE ( K_IF K_NOT K_EXISTS )?
   ( database_name '.' )? table_name
   ( '(' column_def ( ',' column_def )* ( ',' table_constraint )* ')'
   ( K_ROUNDROBIN | K_REPLICATED | ( K_DISTRIBUTED K_BY column_name) )?
   )
 ;

delete_stmt
 : with_clause? K_DELETE K_FROM qualified_table_name 
   ( K_WHERE expr )?
 ;

drop_index_stmt
 : K_DROP K_INDEX ( K_IF K_EXISTS )? ( database_name '.' )? index_name
 ;

drop_table_stmt
 : K_DROP K_TABLE ( K_IF K_EXISTS )? ( database_name '.' )? table_name
 ;

insert_stmt
 : with_clause? K_INSERT K_INTO
   ( database_name '.' )? table_name ( '(' column_name ( ',' column_name )* ')' )?
   ( K_VALUES '(' expr ( ',' expr )* ')' ( ',' '(' expr ( ',' expr )* ')' )*
   | select_stmt
   | K_DEFAULT K_VALUES
   )
 ;

copy_stmt
 : K_COPY table_name ( '(' column_name ( ',' column_name )* ')' )?  
   K_FROM STRING_LITERAL
   ( K_WHERE expr )?
 ;

set_stmt
 : K_SET pragma_name ( '=' config_value )?
 ;

select_stmt
 : ( K_WITH K_RECURSIVE? common_table_expression ( ',' common_table_expression )* )?
   select_core ( compound_operator select_core )*
   ( K_ORDER K_BY ordering_term ( ',' ordering_term )* )?
   ( K_LIMIT expr ( ( K_OFFSET | ',' ) expr )? )?
 ;

update_stmt
 : with_clause? K_UPDATE qualified_table_name
   K_SET column_name '=' expr ( ',' column_name '=' expr )* ( K_WHERE expr )?
 ;

column_def
 : column_name type_name? column_constraint*
 ;

type_name
 : name+ ( '(' signed_number ')'
         | '(' signed_number ',' signed_number ')' )?
 ;

column_constraint
 : ( K_CONSTRAINT name )?
   ( K_PRIMARY K_KEY ( K_ASC | K_DESC )? K_AUTOINCREMENT?
   | K_NOT? K_NULL 
   | K_UNIQUE 
   | K_CHECK '(' expr ')'
   | K_DEFAULT (signed_number | literal_value | '(' expr ')')
   | K_COLLATE collation_name
   | foreign_key_clause
   )
 ;


/*
    SQLite understands the following binary operators, in order from highest to
    lowest precedence:

    ||
    *    /    %
    +    -
    <<   >>   &    |
    <    <=   >    >=
    =    ==   !=   <>   IS   IS NOT   IN   LIKE   GLOB   MATCH   REGEXP
    AND
    OR
*/
logical_expr
 : K_NOT logical_expr                                       #LogicNotExpr
 | logical_expr K_AND logical_expr							#LogicAndExpr
 | logical_expr K_OR logical_expr							#LogicOrExpr
 | pred_expr                                                #predexpr
 | '(' logical_expr ')'										#brackexpr
;

/* every expr here returns a boolean */
pred_expr
 : arith_expr op=( '<' | '<=' | '>' | '>=' ) arith_expr					                                #arithcompexpr
 | arith_expr op=( '=' | '==' | '!=' | '<>' | K_GLOB | K_MATCH | K_REGEXP ) arith_expr	                #BoolEqualexpr

 | arith_expr K_NOT? K_BETWEEN  arith_expr K_AND arith_expr #BetweenExpr
 | arith_expr K_IS K_NOT? arith_expr						#IsExpr
 | arith_expr K_NOT? K_LIKE arith_expr						#LikeExpr
 | arith_expr ( K_ISNULL | K_NOTNULL | K_NOT K_NULL )		#NullExpr

 | arith_expr K_NOT? K_IN ( '(' ( select_stmt
                          | arith_expr ( ',' arith_expr )*
                          )? 
                      ')'
                    | ( database_name '.' )? table_name )	#InSubqueryExpr
 | K_EXISTS  '(' select_stmt ')'						    #ExistsSubqueryExpr
 | arith_expr                                               #barithExpr
;

/* every expr here returns a value - if the value is a boolean, arith_expr can be a 
 * predicate, for example: WHERE case when ... then true ... end
 */
arith_expr
 : literal_value											                                                          #xxLiteralExpr
 | unary_operator arith_expr								                                                    #unaryexpr
 | BIND_PARAMETER											                                                          #bindexpr
 | ( ( database_name '.' )? table_name '.' )? column_name	                                      #ColExpr
 | signed_number                                                                                #NumericLiteral
 | arith_expr op=( '*' | '/' | '%' ) arith_expr				                                          #arithtimesexpr
 | arith_expr op=( PLUS | MINUS ) arith_expr				                                            #arithplusexpr
 | arith_expr op=( '<<' | '>>' | '&' | '|' ) arith_expr		                                      #arithbitexpr
 | '(' select_stmt ')'                                                                          #ScalarSubqueryExpr
 | function_name '(' ( K_DISTINCT? arith_expr ( ',' arith_expr )* | '*' )? ')'	                #FuncExpr
 | K_CAST '(' arith_expr K_AS type_name ')'					                                            #CastExpr
 | K_CASE arith_expr? ( K_WHEN logical_expr K_THEN arith_expr )+ ( K_ELSE arith_expr )? K_END		#CaseExpr
 | arith_expr '||' arith_expr											                                              #strconexpr
 | '(' arith_expr ')'										                                                        #arithbrackexpr
 ;

expr
 :
 logical_expr               #logicalexpr
 | arith_expr               #arithexpr
 | '(' expr ')'							#otherbrackexpr
 ;

foreign_key_clause
 : K_REFERENCES foreign_table ( '(' column_name ( ',' column_name )* ')' )?
 ;

indexed_column
 : column_name ( K_COLLATE collation_name )? ( K_ASC | K_DESC )?
 ;
 
table_constraint
: ( K_PRIMARY K_KEY | K_UNIQUE )  '(' indexed_column ( ',' indexed_column )* ')'    #PrimaryKeyConstraint
   | K_CHECK '(' expr ')'                                                           #CheckConstraint
   | K_FOREIGN K_KEY '(' column_name ( ',' column_name )* ')' foreign_key_clause    #ForeignKeyConstraint
 ;

with_clause
 : K_WITH K_RECURSIVE? cte_table_name K_AS '(' select_stmt ')' ( ',' cte_table_name K_AS '(' select_stmt ')' )*
 ;

qualified_table_name
 : ( database_name '.' )? table_name ( K_INDEXED K_BY index_name
                                     | K_NOT K_INDEXED )?
 ;

ordering_term
 : expr ( K_COLLATE collation_name )? ( K_ASC | K_DESC )?
 ;

config_value
 : signed_number
 | name
 | STRING_LITERAL
 ;

common_table_expression
 : table_name ( '(' column_name ( ',' column_name )* ')' )? K_AS '(' select_stmt ')'
 ;

result_column
 : '*'
 | table_name '.' '*'
 | expr ( K_AS? column_alias )?
 ;

table_or_subquery
 : ( database_name '.' )? table_name ( K_AS? table_alias )? (tablesample_clause)?  #fromSimpleTable
 | '(' ( table_or_subquery ( ',' table_or_subquery )*
       | join_clause )
   ')' ( K_AS? table_alias_with_columns )?	(tablesample_clause)? 	               #fromJoinTable
 | '(' select_stmt ')' ( K_AS? table_alias_with_columns )?	(tablesample_clause)?  #fromSelectStmt
 ;

table_alias_with_columns
 : table_alias ( '(' column_name ( ',' column_name )* ')' )?
 ;

join_clause
 : table_or_subquery ( join_operator table_or_subquery join_constraint )*
 ;

join_operator
 : K_NATURAL? ( K_LEFT K_OUTER? | K_RIGHT K_OUTER? | K_FULL K_OUTER? | K_INNER | K_CROSS )? K_JOIN
 ;

join_constraint
 : ( K_ON expr
   | K_ON '(' expr ')'
   | K_USING '(' column_name ( ',' column_name )* ')' )?
 ;

select_core
 : K_SELECT ( K_DISTINCT | K_ALL )? result_column ( ',' result_column )*
   ( K_FROM ( table_or_subquery ( ',' table_or_subquery )* | join_clause ) )?
   ( K_WHERE expr )?
   ( K_GROUP K_BY expr ( ',' expr )* )? ( K_HAVING expr )?
 | K_VALUES '(' expr ( ',' expr )* ')' ( ',' '(' expr ( ',' expr )* ')' )*
 ;

tablesample_clause
: K_TABLESAMPLE ( K_ROW | K_PERCENT ) '(' signed_number ')'
;

compound_operator
 : K_UNION
 | K_UNION K_ALL
 | K_INTERSECT
 | K_INTERSECT K_ALL
 | K_EXCEPT
 | K_EXCEPT K_ALL
 ;

cte_table_name
 : table_name ( '(' column_name ( ',' column_name )* ')' )?
 ;

signed_number
 : ( '+' | '-' )? NUMERIC_LITERAL
 ;

// FIXME: can't use day|month|year for some reason
date_unit_single
: any_name
;

date_unit_plural
: ( 'days' | 'months' | 'years' )
;

literal_value
: signed_number  date_unit_plural            #DateLiteral
 | K_DATE STRING_LITERAL                        #DateStringLiteral
 | K_INTERVAL STRING_LITERAL date_unit_single   #IntervalLiteral
 | STRING_LITERAL                               #StringLiteral
 | K_NULL                                       #NullLiteral
 | ( K_CURRENT_TIME | K_CURRENT_DATE | K_CURRENT_TIMESTAMP)  #CurrentTimeLiteral
 ;

unary_operator
 : '-'
 | '+'
 | '~'
 | K_NOT
 ;

error_message
 : STRING_LITERAL
 ;

module_argument // TODO check what exactly is permitted here
 : expr
 | column_def
 ;

column_alias
 : IDENTIFIER
 | STRING_LITERAL
 ;

keyword
 : K_ADD
 | K_AFTER
 | K_ALL
 | K_ALTER
 | K_ANALYZE
 | K_AND
 | K_AS
 | K_ASC
 | K_ATTACH
 | K_AUTOINCREMENT
 | K_BEFORE
 | K_BEGIN
 | K_BETWEEN
 | K_BY
 | K_CASCADE
 | K_CASE
 | K_CAST
 | K_CHECK
 | K_COLLATE
 | K_COLUMN
 | K_COMMIT
 | K_CONFLICT
 | K_CONSTRAINT
 | K_CREATE
 | K_CROSS
 | K_CURRENT_DATE
 | K_CURRENT_TIME
 | K_CURRENT_TIMESTAMP
 | K_DATABASE
 | K_DATE
 | K_DEFAULT
 | K_DELETE
 | K_DESC
 | K_DISTRIBUTED
 | K_DISTINCT
 | K_DROP
 | K_EACH
 | K_ELSE
 | K_END
 | K_ESCAPE
 | K_EXCEPT
 | K_EXCLUSIVE
 | K_EXECUTE
 | K_EXISTS
 | K_EXPLAIN
 | K_FOR
 | K_FOREIGN
 | K_FROM
 | K_FULL
 | K_GLOB
 | K_GROUP
 | K_HAVING
 | K_IF
 | K_IN
 | K_INDEX
 | K_INDEXED
 | K_INITIALLY
 | K_INNER
 | K_INSERT
 | K_INSTEAD
 | K_INTERSECT
 | K_INTERVAL
 | K_INTO
 | K_IS
 | K_ISNULL
 | K_JOIN
 | K_KEY
 | K_LEFT
 | K_LIKE
 | K_LIMIT
 | K_MATCH
 | K_NATURAL
 | K_NO
 | K_NOT
 | K_NOTNULL
 | K_NULL
 | K_OF
 | K_OFFSET
 | K_ON
 | K_OR
 | K_ORDER
 | K_OUTER
 | K_PRAGMA
 | K_PRIMARY
 | K_QUERY
 | K_RAISE
 | K_RECURSIVE
 | K_REFERENCES
 | K_REGEXP
 | K_RIGHT
 | K_ROW
 | K_SELECT
 | K_SET
 | K_TABLE
 | K_THEN
 | K_TO
 | K_UNION
 | K_UNIQUE
 | K_UPDATE
 | K_USING
 | K_VALUES
 | K_VIEW
 | K_WHEN
 | K_WHERE
 | K_WITH
 ;

// TODO check all names below

name
 : any_name
 ;

function_name
 : any_name
 ;

database_name
 : any_name
 ;

table_name 
 : any_name
 ;

table_or_index_name 
 : any_name
 ;

new_table_name 
 : any_name
 ;

column_name 
 : non_key_name
 ;

collation_name 
 : any_name
 ;

foreign_table 
 : any_name
 ;

index_name 
 : any_name
 ;

pragma_name 
 : any_name
 ;

savepoint_name 
 : any_name
 ;

table_alias 
 : non_key_name
 ;

transaction_name
 : any_name
 ;

non_key_name
 : IDENTIFIER
 ;

any_name
 : IDENTIFIER 
 | keyword
 | STRING_LITERAL
 | '(' any_name ')'
 ;

SCOL : ';';
DOT : '.';
OPEN_PAR : '(';
CLOSE_PAR : ')';
COMMA : ',';
ASSIGN : '=';
STAR : '*';
PLUS : '+';
MINUS : '-';
TILDE : '~';
PIPE2 : '||';
DIV : '/';
MOD : '%';
LT2 : '<<';
GT2 : '>>';
AMP : '&';
PIPE : '|';
LT : '<';
LT_EQ : '<=';
GT : '>';
GT_EQ : '>=';
EQ : '==';
NOT_EQ1 : '!=';
NOT_EQ2 : '<>';

// http://www.sqlite.org/lang_keywords.html
K_ADD : A D D;
K_AFTER : A F T E R;
K_ALL : A L L;
K_ALTER : A L T E R;
K_ANALYZE : A N A L Y Z E;
K_AND : A N D;
K_AS : A S;
K_ASC : A S C;
K_ATTACH : A T T A C H;
K_AUTOINCREMENT : A U T O I N C R E M E N T;
K_BEFORE : B E F O R E;
K_BEGIN : B E G I N;
K_BETWEEN : B E T W E E N;
K_BY : B Y;
K_CASCADE : C A S C A D E;
K_CASE : C A S E;
K_CAST : C A S T;
K_CHECK : C H E C K;
K_COLLATE : C O L L A T E;
K_COLUMN : C O L U M N;
K_COMMIT : C O M M I T;
K_CONFLICT : C O N F L I C T;
K_CONSTRAINT : C O N S T R A I N T;
K_COPY : C O P Y;
K_CREATE : C R E A T E;
K_CROSS : C R O S S;
K_CURRENT_DATE : C U R R E N T '_' D A T E;
K_CURRENT_TIME : C U R R E N T '_' T I M E;
K_CURRENT_TIMESTAMP : C U R R E N T '_' T I M E S T A M P;
K_DATABASE : D A T A B A S E;
K_DATE : D A T E;
K_DEFAULT : D E F A U L T;
K_DELETE : D E L E T E;
K_DESC : D E S C;
K_DISTINCT : D I S T I N C T;
K_DISTRIBUTED: D I S T R I B U T E D;
K_DROP : D R O P;
K_EACH : E A C H;
K_ELSE : E L S E;
K_END : E N D;
K_ESCAPE : E S C A P E;
K_EXCEPT : E X C E P T;
K_EXCLUSIVE : E X C L U S I V E;
K_EXECUTE : E X E C U T E;
K_EXISTS : E X I S T S;
K_EXPLAIN : E X P L A I N;
K_FOR : F O R;
K_FOREIGN : F O R E I G N;
K_FROM : F R O M;
K_FULL : F U L L;
K_GLOB : G L O B;
K_GROUP : G R O U P;
K_HAVING : H A V I N G;
K_IF : I F;
K_IN : I N;
K_INDEX : I N D E X;
K_INDEXED : I N D E X E D;
K_INITIALLY : I N I T I A L L Y;
K_INNER : I N N E R;
K_INSERT : I N S E R T;
K_INSTEAD : I N S T E A D;
K_INTERSECT : I N T E R S E C T;
K_INTERVAL : I N T E R V A L;
K_INTO : I N T O;
K_IS : I S;
K_ISNULL : I S N U L L;
K_JOIN : J O I N;
K_KEY : K E Y;
K_LEFT : L E F T;
K_LIKE : L I K E;
K_LIMIT : L I M I T;
K_MATCH : M A T C H;
K_NATURAL : N A T U R A L;
K_NO : N O;
K_NOT : N O T;
K_NOTNULL : N O T N U L L;
K_NULL : N U L L;
K_OF : O F;
K_OFFSET : O F F S E T;
K_ON : O N;
K_OR : O R;
K_ORDER : O R D E R;
K_OUTER : O U T E R;
K_PERCENT : P E R C E N T;
K_PLAN : P L A N;
K_PRAGMA : P R A G M A;
K_PRIMARY : P R I M A R Y;
K_PARTITION: P A R T I T I O N;
K_QUERY : Q U E R Y;
K_RAISE : R A I S E;
K_RECURSIVE : R E C U R S I V E;
K_REFERENCES : R E F E R E N C E S;
K_REGEXP : R E G E X P;
K_RENAME : R E N A M E;
K_REPLACE : R E P L A C E;
K_REPLICATED: R E P L I C A T E D;
K_ROUNDROBIN: R O U N D R O B I N;
K_RESTRICT : R E S T R I C T;
K_RIGHT : R I G H T;
K_ROW : R O W;
K_SELECT : S E L E C T;
K_SET : S E T;
K_TABLE : T A B L E;
K_TABLESAMPLE : T A B L E S A M P L E;
K_THEN : T H E N;
K_TO : T O;
K_UNION : U N I O N;
K_UNIQUE : U N I Q U E;
K_UPDATE : U P D A T E;
K_USING : U S I N G;
K_VACUUM : V A C U U M;
K_VALUES : V A L U E S;
K_VERBOSE: V E R B O S E;
K_VIEW : V I E W;
K_VIRTUAL : V I R T U A L;
K_WHEN : W H E N;
K_WHERE : W H E R E;
K_WITH : W I T H;
K_WITHOUT : W I T H O U T;

IDENTIFIER
 : '"' (~'"' | '""')* '"'
 | '`' (~'`' | '``')* '`'
 | '[' ~']'* ']'
 | [a-zA-Z_] [a-zA-Z_0-9]* // TODO check: needs more chars in set
 ;

NUMERIC_LITERAL
 : DIGIT+ ( '.' DIGIT* )? ( E [-+]? DIGIT+ )?
 | '.' DIGIT+ ( E [-+]? DIGIT+ )?
 ;

BIND_PARAMETER
 : '?' DIGIT*
 | [:@$] IDENTIFIER
 ;

STRING_LITERAL
 : '\'' ( ~'\'' | '\'\'' )* '\''
 ;

 DATE_LITERAL
 : NUMERIC_LITERAL K_DATE
 ;

BLOB_LITERAL
 : X STRING_LITERAL
 ;

SINGLE_LINE_COMMENT
 : '--' ~[\r\n]* -> channel(HIDDEN)
 ;

MULTILINE_COMMENT
 : '/*' .*? ( '*/' | EOF ) -> channel(HIDDEN)
 ;

SPACES
 : [ \u000B\t\r\n] -> channel(HIDDEN)
 ;

UNEXPECTED_CHAR
 : .
 ;

fragment DIGIT : [0-9];

fragment A : [aA];
fragment B : [bB];
fragment C : [cC];
fragment D : [dD];
fragment E : [eE];
fragment F : [fF];
fragment G : [gG];
fragment H : [hH];
fragment I : [iI];
fragment J : [jJ];
fragment K : [kK];
fragment L : [lL];
fragment M : [mM];
fragment N : [nN];
fragment O : [oO];
fragment P : [pP];
fragment Q : [qQ];
fragment R : [rR];
fragment S : [sS];
fragment T : [tT];
fragment U : [uU];
fragment V : [vV];
fragment W : [wW];
fragment X : [xX];
fragment Y : [yY];
fragment Z : [zZ];

