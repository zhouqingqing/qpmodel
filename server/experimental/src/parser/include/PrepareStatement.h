#ifndef __PREPARESTATEMENT_H_
#define __PREPARESTATEMENT_H_
namespace andb {
struct PrepareStatement : public SQLStatement {
    PrepareStatement ();
    ~PrepareStatement () override;

    char* name;

    // The query that is supposed to be prepared.
    char* query;

	virtual LogicNode* CreatePlan (void) { throw "illegal virtual call"; }
};
}
#endif // __PREPARESTATEMENT_H_
