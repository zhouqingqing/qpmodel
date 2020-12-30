#include  <algorithm>

#include "common/platform.h"
#include "parser/include/expr.h"
#include "parser/include/stmt.h"
#include "parser/include/sqlparserresult.h"

namespace andb {

SQLParserResult::SQLParserResult() :
   isValid_(false),
   errorMsg_(nullptr) {};

SQLParserResult::SQLParserResult(SQLStatement* stmt) :
   isValid_(false),
   errorMsg_(nullptr) {
      addStatement(stmt);
   };

// Move constructor.
SQLParserResult::SQLParserResult(SQLParserResult&& moved) {
   isValid_ = moved.isValid_;
   errorMsg_ = moved.errorMsg_;
   statements_ = std::move(moved.statements_);

   moved.errorMsg_ = nullptr;
   moved.reset();
}

SQLParserResult::~SQLParserResult() {
   reset();
}

void SQLParserResult::addStatement(SQLStatement* stmt) {
   statements_.push_back(stmt);
}

const SQLStatement* SQLParserResult::getStatement(size_t index) const {
   return statements_[index];
}

SQLStatement* SQLParserResult::getMutableStatement(size_t index) {
   return statements_[index];
}

size_t SQLParserResult::size() const {
   return statements_.size();
}

bool SQLParserResult::isValid() const {
   return isValid_;
}

const char* SQLParserResult::errorMsg() const {
   return errorMsg_;
}

int SQLParserResult::errorLine() const {
   return errorLine_;
}

int SQLParserResult::errorColumn() const {
   return errorColumn_;
}

void SQLParserResult::setIsValid(bool isValid) {
   isValid_ = isValid;
}

void SQLParserResult::setErrorDetails(const char* errorMsg, int errorLine, int errorColumn) {
   errorMsg_ = DBG_NEW char[strlen(errorMsg) + 1];
   strcpy ((char *)errorMsg_, errorMsg);
   errorLine_ = errorLine;
   errorColumn_ = errorColumn;
}

const std::vector<SQLStatement*>& SQLParserResult::getStatements() const {
   return statements_;
}

std::vector<SQLStatement*> SQLParserResult::releaseStatements() {
   std::vector<SQLStatement*> copy = statements_;

   statements_.clear();

   return copy;
}

void SQLParserResult::reset()
{
#ifdef __LATER
    // this cleanup should/would have been already done
    // before returning from the parser.
   for (SQLStatement* statement : statements_) {
      delete statement++;
   }
#endif

   statements_.clear();

   isValid_ = false;

   delete [] errorMsg_;
   errorMsg_ = nullptr;

   errorLine_ = -1;
   errorColumn_ = -1;
}

// Does NOT take ownership.
void SQLParserResult::addParameter(Expr* parameter) {
   parameters_.push_back(parameter);
   std::sort(parameters_.begin(), parameters_.end(),
         [](const Expr * a, const Expr * b) {
         return a->ival < b->ival;
         });
}

const std::vector<Expr*>& SQLParserResult::parameters() {
   return parameters_;
}
}
