template <typename Fn>
void PhysicScan::ExecT (Fn&& callback) {
    auto *logic = static_cast<LogicScan*> (logic_);
    auto *r = new Row (1);
    for (int i = 0; i < logic->targetcnt_; i++) {
        (*r)[0] = i;
        
        // apply filter
        auto *filter = logic->filter_;
        if (filter != nullptr) {
            auto result = eval_.Exec (r);
            if (std::get<Bool> (result) == false) continue;
        }
        if (callback (r)) r = new Row (1);
    }
    delete r;
}

template <typename Fn>
void PhysicHashJoin::ExecT (Fn&& callback) {
    std::unordered_map<int, std::vector<Row*>> map;

    // build stage
    children_[0]->Exec ([&] (Row* l) {
        auto key = std::get<int> ((*l)[0]);
        auto search = map.find (key);
        if (search != map.end ()) {
            search->second.emplace_back (l);
        } else {
            map[key] = std::vector<Row*> ();
            map[key].emplace_back (l);
        }

        // this row is owned by hash join now
        return true;
    });

    // probe stage
    children_[1]->Exec ([&] (Row* l) {
        bool owned = false;
        auto key = std::get<int> ((*l)[0]);
        auto search = map.find (key);
        if (search != map.end ()) {
            auto& list = search->second;
            for (auto *a : list) {
                owned |= callback (a);
            }
        }
        return owned;
    });

    // release source
    for (auto& a : map) {
        auto& list = a.second;
        for (auto *b : list) {
            delete b;
        }
    }
    map.clear ();
}

template <typename Fn>
void PhysicAgg::ExecT (Fn&& callback) {
    int sum = 0;
    children_[0]->Exec ([&] (Row* l) {
        sum += std::get<int> ((*l)[0]);
        return false;
    });

    Row* r = new Row (1);
    (*r)[0] = sum;
    if (!callback (r)) delete r;
}
