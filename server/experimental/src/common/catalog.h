#pragma once
#include <string>
#include <utility>
#include <map>

#include "common/Statistics.h"
#include "common/common.h"
#include "common/dbcommon.h"

namespace andb {

// NOTE: None of the operations here are Multi-Thread (MT) safe.
// Tables are not aware of a SCHEMA, yet.
class SystemTable : public UseCurrentResource {};

class NameCompare {
    public :
        bool operator()(const std::string *lv, const std::string *rv) const
        {
            return lv->compare (*rv) < 0;
        }
};

class SysTable : public SystemTable {
public:

    SysTable() {
    }

    bool CreateTable (std::string* tabName, std::vector<ColumnDef*>* columns,
                      std::string* distBy = nullptr);

    TableDef* TryTable (std::string* tblName) {
        auto it = records_.find (tblName);
        if (it != records_.end ()) return it->second;

        return nullptr;
    }

    ColumnDef* Column (std::string* colName, std::string* tblName) {
        TableDef* td = TryTable (colName);
        if (td != nullptr) {
            auto it = td->columns_->find (colName);
            if (it != td->columns_->end ()) return it->second;
        }

        return nullptr;
    }

private:
    std::map<std::string*, TableDef*, NameCompare> records_;
};

class Catalog : public UseCurrentResource {
public:
    static void Init () {
        createOptimizerTestTables ();
        createBuiltInTestTables ();
    }

    static void createOptimizerTestTables ();
    static void createBuiltInTestTables ();
    static int Next ();

    static RandomDevice rand_;
    static SysTable* systable_;
    static SysStats* sysstat_;
};
}  // namespace andb
