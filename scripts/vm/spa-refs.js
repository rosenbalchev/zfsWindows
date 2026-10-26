"use strict";
// WinDbg/kd script for the driver test VM: list every imported pool and
// the holders of its spa_refcount (needs reference_tracking_enable=1 in
// HKLM\SYSTEM\CurrentControlSet\Services\OpenZFS\zfs before the import).
//
//   .scriptload spa-refs.js
//   dx @$scriptContents.spaRefs()

function initializeScript() {
    return [new host.apiVersionSupport(1, 7)];
}

function avlNodes(tree) {
    // In-order walk of an avl_tree_t; yields addresses of the containing
    // objects (node address minus avl_offset).
    var out = [];
    var offset = tree.avl_offset;
    var stack = [];
    var node = tree.avl_root;
    while (!node.isNull || stack.length > 0) {
        while (!node.isNull) {
            stack.push(node);
            node = node.avl_child[0];
        }
        node = stack.pop();
        out.push(node.address.subtract(offset));
        node = node.avl_child[1];
    }
    return out;
}

function describe(addr) {
    var text = "";
    try {
        text = host.memory.readString(addr, 64);
    } catch (e) {
        text = "";
    }
    var sym = "";
    try {
        var lines = host.namespace.Debugger.Utility.Control.ExecuteCommand(
            "ln " + addr.toString(16));
        for (var line of lines) {
            if (line.indexOf("!") >= 0) { sym = line.trim(); break; }
        }
    } catch (e) {
    }
    if (/^[\x20-\x7e]{3,}$/.test(text)) return "\"" + text + "\" " + sym;
    return sym;
}

// Holders of any zfs_refcount_t, e.g.
//   dx @$scriptContents.refHolders(&((OpenZFS!dmu_buf_impl_t*)0x...)->db_holds)
function refHolders(rcPtr) {
    var rc = rcPtr.dereference();
    var out = { count: rc.rc_count, tracked: rc.rc_tracked, holders: [] };
    if (rc.rc_tracked) {
        for (var refAddr of avlNodes(rc.rc_tree)) {
            var ref = host.createPointerObject(refAddr, "OpenZFS",
                "reference_t*");
            out.holders.push(ref.ref_holder.address.toString(16) + " x" +
                ref.ref_number + " " + describe(ref.ref_holder.address));
        }
    }
    return out;
}

// Elements of a list_t (list_node_t embedded at list_offset).
function listItems(list, typeName) {
    var out = [];
    var head = list.list_head.address;
    var offset = list.list_offset;
    var node = list.list_head.list_next;
    var guard = 0;
    while (node.address.compareTo(head) != 0 && guard++ < 100000) {
        out.push(host.createPointerObject(node.address.subtract(offset),
            "OpenZFS", typeName + "*"));
        node = node.list_next;
    }
    return out;
}

// Objsets waiting for eviction on each pool, with their remaining dnodes:
// an objset leaves spa_evicting_os only once all of its dnodes are gone.
function evicting() {
    var result = [];
    var tree = host.getModuleSymbol("OpenZFS", "spa_namespace_avl");
    for (var spaAddr of avlNodes(tree)) {
        var spa = host.createPointerObject(spaAddr, "OpenZFS", "spa_t*");
        for (var os of listItems(spa.spa_evicting_os_list, "objset_t")) {
            var entry = {
                pool: host.memory.readString(spa.spa_name.address),
                objset: os.address.toString(16),
                dataset: os.os_dsl_dataset.isNull ? "(mos)" :
                    os.os_dsl_dataset.ds_object.toString(),
                dnodes: []
            };
            for (var dn of listItems(os.os_dnodes, "dnode_t")) {
                entry.dnodes.push("obj " + dn.dn_object + " type " +
                    dn.dn_type + " holds " + dn.dn_holds.rc_count +
                    " dbufs " + dn.dn_dbufs_count + " @" +
                    dn.address.toString(16));
            }
            result.push(entry);
        }
    }
    return result;
}

function spaRefs() {
    var result = [];
    var tree = host.getModuleSymbol("OpenZFS", "spa_namespace_avl");
    for (var spaAddr of avlNodes(tree)) {
        var spa = host.createPointerObject(spaAddr, "OpenZFS", "spa_t*");
        var rc = spa.spa_refcount;
        var entry = {
            name: host.memory.readString(spa.spa_name.address),
            spa: spaAddr.toString(16),
            count: rc.rc_count,
            tracked: rc.rc_tracked,
            holders: []
        };
        if (rc.rc_tracked) {
            for (var refAddr of avlNodes(rc.rc_tree)) {
                var ref = host.createPointerObject(refAddr, "OpenZFS",
                    "reference_t*");
                entry.holders.push(ref.ref_holder.address.toString(16) +
                    " x" + ref.ref_number + " " +
                    describe(ref.ref_holder.address));
            }
        }
        result.push(entry);
    }
    return result;
}
