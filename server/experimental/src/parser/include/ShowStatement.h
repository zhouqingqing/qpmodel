#ifndef __SHOWSTATEMENT_H_
#define __SHOWSTATEMENT_H_
namespace andb {
struct ShowStatement : public SQLStatement {
    ShowStatement (ShowType type);
    ~ShowStatement () override;

    ShowType type;
    char* schema;
    char* name;
	virtual LogicNode* CreatePlan (void) { throw "illegal virtual call"; }
};
}
#endif // __SHOWSTATEMENT_H_
