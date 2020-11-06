#ifndef __TRANSACTIONSTATEMENT_H_
#define __TRANSACTIONSTATEMENT_H_
namespace andb {
struct TransactionStatement : public SQLStatement {
    enum TransactionCommand { kBeginTransaction, kCommitTransaction, kRollbackTransaction };

    TransactionStatement (TransactionCommand command);
    ~TransactionStatement () override;

    TransactionCommand command;
	virtual LogicNode* CreatePlan (void) { throw "illegal virtual call"; }
};
}
#endif // __TRANSACTIONSTATEMENT_H_
