#pragma once
#include "common/Statistics.h"
#include "common/common.h"
#include "common/dbcommon.h"

namespace andb {

// NOTE: None of the operations here are Multi-Thread (MT) safe.

class SystemTable : public UseCurrentResource {};

class SysTable : public SystemTable {
public:
    bool CreateTable (const std::string* tabName, std::vector<ColumnDef*>* columns,
                      std::string* distBy = nullptr);

    TableDef* TryTable (const std::string* tblName) {
        auto it = records_.find (tblName);
        if (it != records_.end ()) return it->second;

        return nullptr;
    }

    ColumnDef* Column (const std::string* colName, const std::string* tblName) {
        TableDef* td = TryTable (colName);
        if (td != nullptr) {
            auto it = td->columns_->find (colName);
            if (it != td->columns_->end ()) return it->second;
        }

        return nullptr;
    }

private:
    std::map<const std::string*, TableDef*> records_;
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
