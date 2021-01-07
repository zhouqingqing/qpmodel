template<typename Fn>
void PhysicScan::ExecT(Fn&& callback)
{
    auto* logic  = static_cast<LogicScan*>(logic_);
    int   distId = 0; // no distribtution yet.
    auto  heap   = getSourceReader(distId);
    for (auto r : *heap) {
        // apply filter
        auto* filter = logic->filter_;
        if (filter != nullptr) {
            auto result = eval_.Exec(r);
            if (std::get<Bool>(result) == false)
                continue;
        }
        // not createing a copy for reader: if (callback (r)) r = new Row (1);
        callback(r);
    }
    // delete r;
    callback(nullptr); // EOF
}

template<typename Fn>
void PhysicHashJoin::ExecT(Fn&& callback)
{
    std::unordered_map<int, std::vector<Row*>> map;

    // build stage
    children_[0]->Exec([&](Row* l) {
        if (l != nullptr && !l->Empty()) {
            auto key    = std::get<int>((*l)[0]);
            auto search = map.find(key);
            if (search != map.end()) {
                search->second.emplace_back(l);
            } else {
                map[key] = std::vector<Row*>();
                map[key].emplace_back(l);
            }

            // this row is owned by hash join now
            return true;
        }
        return false;
    });

    // probe stage
    children_[1]->Exec([&](Row* l) {
        if (l != nullptr && !l->Empty()) {
            bool owned  = false;
            auto key    = std::get<int>((*l)[0]);
            auto search = map.find(key);
            if (search != map.end()) {
                auto& list = search->second;
                for (auto* a : list) {
                    owned |= callback(a);
                }
            }
            return owned;
        }

        return false;
    });

#if defined(__USE_ROWCOPY_)
    // At the moment only the tableDef owns the rows and at the top level
    // rows are copied into user space, so this should not be done.
    // if and when the need arises to modify the rows, make a copy
    // and delete here.
    // release source
    for (auto& a : map) {
        auto& list = a.second;
        for (auto* b : list) {
            delete b;
        }
    }
#endif // defined(__USE_ROWCOPY_)
    map.clear();
}

template<typename Fn>
void PhysicAgg::ExecT(Fn&& callback)
{
    int sum = 0;
    children_[0]->Exec([&](Row* l) {
        if (l != nullptr && !l->Empty()) {
            sum += std::get<int>((*l)[0]);
        }
        return false;
    });

    Row* r  = new Row(1);
    (*r)[0] = sum;
    if (!callback(r))
        delete r;
}
