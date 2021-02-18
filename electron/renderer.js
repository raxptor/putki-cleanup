const { ipcRenderer } = require('electron');
const dataloader = require('./dataloader');
const datawriter = require('./datawriter')
const popups = require('./popups');
const path = require('path');
const fs = require('fs');
const databrowser = require('./databrowser');
var Dialogs = require("dialogs");
var dialogs = new Dialogs({});
var reload_browser = function() { console.log("Not reloading browser"); }

var Primitive = {
    "String": {
        Editor: "String"
    },
    "Text": {
        Editor: "Text"
    },
    "Bool": {
        Editor: "Checkbox"
    },
    "Hash": {
        Editor: "Hash"
    }, 
    "Int": {
        Editor: "Int"
    },
    "I32": {
        Editor: "Int"
    },
    "U32": {
        Editor: "Int"
    },
    "Float": {
        Editor: "Int"
    },
    "U8": {
        Editor: "Int"
    }
}

function on_change(ed)
{
    ipcRenderer.send("change");
    document.title = Configuration.data["title"] + " [unsaved changes]";
}

function clone(src) {
    console.log("clone", src, JSON.stringify(src), " and reparse ", JSON.parse(JSON.stringify(src)));
    var obj = JSON.parse(JSON.stringify(src));
    delete obj._path;
    return obj;
}

function resolve_type(type)
{
    if (UserTypes[type] !== undefined)
        return UserTypes[type];
    return Primitive[type];
}

function default_value(field, is_array_element)
{
    if (field.Default !== undefined)
        return field.Default;
    if (field.Array && !is_array_element)
        return [];
    if (field.Pointer)
        return null;
    var type = resolve_type(field.Type);
    if (type.Fields !== undefined)
        return {};
    if (type.Editor == "Int")
        return 0;
    if (type.Editor == "String" || type.Editor == "Text" || type.Editor == "Hash")
        return "";
    if (type.Values !== undefined && type.Values.length > 0)
        return type.Values[0].Name;
}

function mk_button(command, fn)
{
    var _input = document.createElement('input');
    _input.type = "submit";
    _input.id = command;
    _input.name = command;
    _input.value = command;
    _input.addEventListener("click", fn);
    return _input;
}

function create_array_editor(ed, args)
{
    var expanded = (args || {}).expanded || [];
    var iv = ed.data[ed.field.Name];
    var _array = document.createElement('x-array');

    if (iv === undefined)
    {
        iv = [];
        ed.data[ed.field.name] = iv;
    } 
    
    var row = 1;
    for (var i=0;i<=iv.length;i++)
    {   
        if (i < iv.length)
        {
            var ctl0 = document.createElement('x-array-controls');
            ctl0.appendChild(mk_button("mv", function(idx) { 
                return function() {
                    dialogs.prompt("Move to index", idx, function(val) {
                        if (val != null)
                        {
                            var org = iv[idx];
                            iv.splice(idx, 1);
                            iv.splice(val, 0, org);
                            on_inline_changed(_array._x_reload());
                            on_change();
                        }
                    });
                }; } (i)
            ));            
            ctl0.appendChild(mk_button("clone", function(idx) { 
                return function() {
                    iv.splice(idx + 1, 0, clone(iv[idx]));
                    on_inline_changed(_array._x_reload());
                    on_change();
                }; } (i)
            ));
            ctl0.appendChild(mk_button("del", function(idx) { 
                return function() {
                    iv.splice(idx, 1);
                    on_inline_changed(_array._x_reload());
                    on_change();
                }; } (i)
            ));
            ctl0.style.gridRow = row;
            var _idx = document.createElement('x-prop-index');
            _idx.appendChild(document.createTextNode(i.toString()));
            _idx.style.gridRow = row;
            _array.appendChild(_idx);
            row = create_property(_array, row, {
                data: ed.data[ed.datafield],
                field: ed.field,
                parent: ed.parent,
                datafield: i
            }, true, expanded.indexOf(i) != -1);
            _array.appendChild(ctl0); 
        }
        else
        {
            var ctl0 = document.createElement('x-array-bottom-controls');
            ctl0.style.gridRow = row;
            ctl0.appendChild(mk_button("add empty", function() { 
                return function() {
                    iv.splice(iv.length, 0, default_value(ed.field, true));
                    on_inline_changed(_array._x_reload({ expanded: [iv.length-1] }));
                    on_change();
                }; } (i)));
            _array.appendChild(ctl0); 

            if (ed.field.Pointer) {
                var ptrnew = mk_button("add new embed", function() {
                    popups.ask_type(UserTypes, ed.field.Type, function(seltype) {
                        iv.splice(iv.length, 0, { _type: seltype });
                        on_inline_changed(_array._x_reload({ expanded: [iv.length-1] }));
                        on_change();
                    });
                });
                var ptrlink = mk_button("add link", function() {
                    popups.ask_instance(UserTypes, Data, ed.field.Type, function(selpath) {
                        iv.splice(iv.length, 0, selpath);
                        on_inline_changed(_array._x_reload({ expanded: [iv.length-1] }));
                        on_change();
                    });
                });                
                ctl0.appendChild(ptrnew);
                ctl0.appendChild(ptrlink);
            }        
        }
    }
    return _array;
}

/*
    Auto complete shit.
*/

var acs = {};
var is_upd = {};
function update_auto_complete(list_id)
{    
    is_upd[list_id]++;
    setTimeout(function() {
        is_upd[list_id]--;
        if (is_upd[list_id] == 0) {
            var old = acs[list_id];
            if (old !== undefined)
                acs[list_id].destroy();            
            // This will find all elements on the page and install the auto complete. So if many request the same auto on many fields,
            // we will only do it when update refcount is 0 and so do it once.            
            acs[list_id] = new autoComplete({minChars:1, selector:(".pac-" + list_id), source: function(term, suggest) {        
                var l = document.getElementById(list_id).options;
                var k = [];
                var lower = term.toLowerCase();
                for (var i=0;i<l.length;i++) {
                    if (l[i].value.toLowerCase().indexOf(term) != -1)
                        k.push(l[i].value);
                }                
                suggest(k);
            }});
        }
    }, 10);
}

window.make_auto_complete = function(element, list_id)
{
    if (is_upd[list_id] === undefined) {
        is_upd[list_id] = 0;
    } 
    element.classList.add("pac-" + list_id);
    update_auto_complete(list_id);
}

function create_pointer_preview(object, default_type)
{
    var descs = [];
    if (object instanceof Object)
    {
        var root = document.createElement('div');

        if (object._type !== undefined) {
            // only add type if it has parent, otherwise it is implied.
            var type = resolve_type(object._type);
            if (type.hasOwnProperty("Parent"))
                descs.push("@" + resolve_type(object._type).PrettyName);
        }
        if (object._path !== undefined && object._path[0] != '&' && object._path.indexOf("guid/") != 0)
            descs.push(object._path);
        descs.push(create_object_preview_txt(object, resolve_type(object._type || default_type)));
        root.appendChild(document.createTextNode(descs.join(' ')));
        return root;
    }
    else    
    {
        var style = document.createElement('span');
        if (object === undefined || object === null)
        {
            style.appendChild(document.createTextNode("null"));
            style.classList.add('nullptr');
            return style;
        }
        else
        {
            style.appendChild(document.createTextNode(object.toString()));
            style.classList.add('shared');
            style.addEventListener('click', function() {
                open_editor(object);
            });          
            return style;            
        }
    }
}

function create_object_preview_txt(object, type)
{
    var txts = [];
    for (var x in type.ExpandedFields)
    {
        if (!type.ExpandedFields[x].ShowInEditor)
            continue;
        var fn = type.ExpandedFields[x].Name;
        var pn = type.ExpandedFields[x].PrettyName;        

        var val = object[fn];
        if (val === undefined || val === null || (val instanceof Array && val.length == 0) ||
            (val instanceof Object && Object.keys(val).length === 0))
            continue;
        if (val instanceof Array)
        {
            if (val.length > 0 && (val[0] != null && val[0].constructor == String))
            {
                txts.push(pn + ":[" + val.join(", ") + "]");
            }
            else
            {
                txts.push(pn + ":[" + val.length + " itms]");
            }
        }
        else if (val instanceof Object)
        {
            if (Object.keys(val).length > 0) {
                var t = resolve_type(type.ExpandedFields[x].Type);
                var preview = create_object_preview_txt(val, t);
                if (preview != "<default>")
                    txts.push(pn + ":{" + preview + "}");
            }
        }
        else
        {
            if (val.constructor == String) {
                var lim = 100;
                if (val.length < lim)
                    txts.push(pn + "=" + "\"" + val + "\"");
                else
                    txts.push(pn + "=" + "\"" + val.substring(0, lim-3) + "...\"");
            }
            else
                txts.push(pn + "=" + val.toString());
        }
    }
    if (txts.length == 0)
        return "<default>";
    else
        return txts.join(', ');
}

function reload_wrapped(make, config)
{
    var new_fn = make;
    if (config && config.post_process) {
        var wrapped = function() {
            var tmp = make();
            tmp = config.post_process(tmp, {ed: config.ed});
            return tmp;
        };
        new_fn = wrapped;
    }
    var preview = new_fn();
    preview._x_reload = function(args) {
        var pn = preview.parentNode;
        var neue = new_fn(args);
        neue._x_changed = preview._x_changed;
        neue._x_context_menu = preview._x_context_menu;
        var transfer = ["array-element"];
        if (preview.classList !== undefined) {
            for (var i=0;i<transfer.length;i++)
                if (preview.classList.contains(transfer[i]))
                    neue.classList.add(transfer[i]);
        }
        if (preview.style !==undefined && preview.style.gridRow !== undefined && neue.style !== undefined)
            neue.style.gridRow = preview.style.gridRow;

        preview._x_destroyed = "I was destroyed through reload_wrapped";
        neue._x_reload = preview._x_reload;
        delete preview._x_reload;        
        pn.removeChild(preview);
        if (preview._x_reloaded)
            preview._x_reloaded(neue);
        preview = neue; 
        pn.appendChild(preview);
        return preview;
    };
    return preview;
}

function on_inline_changed(node)
{
    while (node !== null)
    {
        var next = node.parentNode;
        if (node._x_changed)
        {
            node._x_changed();
        }
        node = next;
    }
}

function create_object_preview(object, type)
{
    var tn = document.createTextNode(create_object_preview_txt(object, type));
    tn._x_is_preview = true;
    return tn;
}

function create_array_preview(arr)
{
    var has_objects = false;
    var pure = [];
    for (var i=0;i<arr.length;i++)
    {
        if (arr[i] instanceof Object)
        {
            if (arr[i]._path !== undefined)
                pure.push(arr[i]._path);
             else
                has_objects = true;
        }
        else
        {
            if (arr[i] == null)
                pure.push("null");
            else
                pure.push(arr[i]);
        }
    }
    if (has_objects)
    {
        return document.createTextNode(arr.length + " item(s)");
    }
    if (arr.length == 0)
    {
        return document.createTextNode("<empty>");
    }
    return document.createTextNode("[" + pure.join(", ") + "]");
}

function create_pointer_editor(ed)
{
    var ptr = document.createElement("x-pointer"); 
    var ptrval = document.createElement("div");
    var iv = ed.data[ed.datafield];
    if (iv instanceof Object) {
        var inl = reload_wrapped(function() { return build_full_entry({
            type: iv._type || ed.field.Type,
            path: iv._path,
            data: iv,
            parent: ed
        }, function (new_path) {
            iv._path = new_path;
        }); });

        if (iv.hasOwnProperty("_anchor")) {
            var anch = document.createElement('a');
            anch.name = iv._anchor;
            ptrval.appendChild(inl);
            anch.appendChild(inl);
            ptrval.appendChild(anch);            
        } else {
            ptrval.appendChild(inl);
        }
    } else {
        var ph = document.createElement('div');
        ph.style.display = 'none';
        return ph;
    }

    ptr.appendChild(ptrval);
    return ptr; 
}

function set_default(obj, field, val)
{
    if (obj[field] === undefined || obj[field] === null)
        obj[field] = val;
}

function def_arr(desc) 
{
    set_default(desc.data, desc.datafield, []);
}

function def_obj(desc) 
{
    set_default(desc.data, desc.datafield, {});
}

function create_type_editor(ed, is_array_element)
{
    var config = {
        ed: ed,
        post_process: PluginFieldPostProcess[ed.field.Id]
    };

    if (PluginFieldCustom[ed.field.Id]) {
        var res = PluginFieldCustom[ed.field.Id];
        var output = {};
        var opts = {
            data: Data,
            on_changed: on_inline_changed
        };
        if (res.block) output.block = reload_wrapped(function(args) { return res.block(ed, args, opts); }, config);
        if (res.inline) output.inline = reload_wrapped(function(args) { return res.inline(ed, args, opts); }, config);
        output.pre_expand = res.pre_expand;
        return output;
    }

    var type = resolve_type(ed.field.Type);
    if (!is_array_element && ed.field.Array)
    {
        return {
            block: reload_wrapped(function(args) { def_arr(ed); return create_array_editor(ed, args); }, config),
            inline: reload_wrapped(function(args) { def_arr(ed); return create_array_preview(ed.data[ed.datafield], args) }, config),
            pre_expand: (ed.data[ed.datafield] || []).length > 0
        };
    }
    if (ed.field.Pointer)
    {
        var preview = reload_wrapped(function() { return create_pointer_preview(ed.data[ed.datafield], ed.field.Type); }, config);
        var block = reload_wrapped(function() { return create_pointer_editor(ed); }, config);
        var value_context_menu = function(dom) {
            var ptypes = popups.compatible_types(UserTypes, ed.field.Type);
            var opts = {};
            for (var x in ptypes)
            {
                opts[x] = { display: ptypes[x].PrettyName, data: x }
            }
            console.log("can inline =", (typeof ed.data[ed.datafield] === 'string'));
            ipcRenderer.send('edit-pointer', { types: opts, can_inline: (typeof ed.data[ed.datafield] === 'string') });
            nextEditPointer = function(arg) {
                if (arg == "@clear") {
                    ed.data[ed.datafield] = default_value(ed.field, is_array_element);
                    on_inline_changed(dom.inline);
                    on_change();
                }
                else if (arg == "@link") {
                    popups.ask_instance(UserTypes, Data, ed.field.Type, function(which) {
                        ed.data[ed.datafield] = which;
                        on_inline_changed(dom.inline);
                        on_change();
                    });
                }
                else if (arg == "@pick-type") {
                    popups.ask_type(UserTypes, ed.field.Type, function(which) {
                        if (which != null && which.length > 0)
                        {
                            ed.data[ed.datafield] = {
                                _type: which
                            };
                            on_inline_changed(dom.inline);
                            on_change();
                        }
                    });
                } else if (arg == "@inline") {
                    var path = ed.data[ed.datafield];
                    if (Data[path] !== undefined)
                    {
                        ed.data[ed.datafield] = Data[path];
                        delete Data[path];
                        on_inline_changed(dom.inline);
                        on_change();
                    }
                } else {
                    ed.data[ed.datafield] = {
                        _type: arg.data
                    };
                    on_inline_changed(dom.inline);
                    on_change();
                }
            }            
        };
        return {
            inline: preview,
            block: block,
            value_context_menu: value_context_menu,
            pre_expand: !ed.field.Array
        }
    }

    if (type.Editor == "String" || type.Editor == "Hash")
    {
        return {
            inline: reload_wrapped(function() {
                var ip = document.createElement("input");
                if (ed.data[ed.datafield] !== undefined)
                    ip.value = ed.data[ed.datafield].replace(/\n/g, "\\n");
                else
                    ip.value = default_value(ed.field, is_array_element);
                ip.addEventListener("change", function() {
                    ed.data[ed.datafield] = ip.value.replace(/\\n/g, "\n");
                    on_inline_changed(ip);
                    on_change();
                });
                return ip;         
            }, config)
        }
    }
    if (type.Editor == "Int")
    {
        return {
            inline: reload_wrapped(function() {
                var ip = document.createElement("input");
                var val = ed.data[ed.datafield];
                if (val === undefined)
                    ip.value = default_value(ed.field, is_array_element);
                else
                    ip.value = val;
                ip.type = "number";
                ip.addEventListener("change", function() {
                    ed.data[ed.datafield] = ip.value;
                    on_inline_changed(ip);
                    on_change();
                });  
                return ip;
            }, config)
        }
    } 
    if (type.Editor == "Text")
    {
        return {
            inline: reload_wrapped(function () {
                var ta = document.createElement("textarea");
                ta.rows = 10;
                ta.value = ed.data[ed.datafield] || "";
                ta.addEventListener("change", function() {
                    ed.data[ed.datafield] = ta.value;
                    on_inline_changed(ta);
                    on_change();
                });
                return ta;
            }, config)
        };
    }
    if (type.Editor == "Checkbox")
    {
        return {
            inline: reload_wrapped(function() {
                var ta = document.createElement("input");
                ta.type = "checkbox";
                var val = ed.data[ed.datafield];
                if (val === undefined)
                    val = default_value(ed.field, is_array_element);
                ta.checked = [true, "true", "True", "1"].indexOf(val) != -1;
                ta.addEventListener("change", function() {
                    ed.data[ed.datafield] = ta.checked;
                    on_inline_changed(ta); 
                    on_change();
                });  
                return ta;
            }, config)
        };
    }
    if (type.Values !== undefined)
    {
        return {
            inline: reload_wrapped(function() {
                var sel = document.createElement("select");
                var val = ed.data[ed.datafield];
                if (val === undefined)
                    val = default_value(ed.field, is_array_element);
                for (var i=0;i<type.Values.length;i++)
                {   
                    var opt = document.createElement("option"); 
                    opt.text = type.Values[i].Name;
                    opt.value = type.Values[i].Value;
                    sel.options.add(opt);
                    if (val == type.Values[i].Name)
                        sel.selectedIndex = i;
                }
                sel.addEventListener("change", function() {
                    for (var x in type.Values)
                    {
                        if (sel.value == type.Values[x].Value)
                        {
                            ed.data[ed.datafield] = type.Values[x].Name; 
                            on_inline_changed(sel);
                            on_change();
                        }
                    }
                });
                return sel;
            }, config)
        }
    }
    if (type.Fields !== undefined)
    {
        return {
            inline: reload_wrapped(function() { def_obj(ed); return create_object_preview(ed.data[ed.datafield], type) }, config ),
            block: reload_wrapped(function() { def_obj(ed); return build_block_entry({
                type: ed.field.Type,
                data: ed.data[ed.datafield],
                parent: ed
            })}, config)
        };
    }
    var el = document.createElement("input");
    el.value = "UNKNOWN TYPE " + ed.field.Type;
    return {
        inline: el
    };
}

// returns row
function create_property(parent, row, objdesc, is_array_element, expanded)
{
    var update_label = function() { };
    var dom = create_type_editor(objdesc, is_array_element);
 
    if (!is_array_element)
    {
        var _prop_name = document.createElement('x-prop-name');
        var disp_prop_name = objdesc.field.PrettyName;
        if (objdesc.field.LocalizationCategory) {
            disp_prop_name = disp_prop_name + "\u00A0{loc}";
        }
        _prop_name.appendChild(document.createTextNode(disp_prop_name));
        _prop_name.style.gridRow = row;
        parent.appendChild(_prop_name);
        update_label = function() {
            _prop_name.classList.remove("no-value");
            var cv = objdesc.data[objdesc.datafield];
            if (cv === undefined || cv === null || (cv instanceof Array && cv.length == 0) ||
                (cv instanceof Object && Object.keys(cv).length === 0))
                _prop_name.classList.add("no-value");
        };
        _prop_name.addEventListener("contextmenu", function() {
            var value = objdesc.data[objdesc.datafield];
            if (value instanceof Array)
            {
                value.splice(0, value.length);
            }
            else if (value instanceof Object)
            {
                for (var x in value)
                    delete value[x];
            }
            else
            {
                delete objdesc.data[objdesc.datafield];
            }
            if (dom.inline && dom.inline._x_reload) {
                dom.inline = dom.inline._x_reload();
                on_inline_changed(dom.inline);
            }
            if (dom.block && dom.block._x_reload) {
                dom.block = dom.block._x_reload();
            }
            update_label();
        });
        update_label();
    }

    var _prop_value;
    if (dom.inline)
    {
        _prop_value = document.createElement('x-prop-value'); 
        _prop_value.appendChild(dom.inline);
        _prop_value.style.gridRow = row;        
        if (is_array_element)
            _prop_value.classList.add("array-element");

        var value = (objdesc.data.hasOwnProperty(objdesc.datafield) && objdesc.data[objdesc.datafield]) || undefined;
        if (value == undefined || (value instanceof Array && value.length == 0))
            _prop_value.classList.add("no-value");

        _prop_value.addEventListener("contextmenu", function() {
            if (dom.value_context_menu)            
                dom.value_context_menu(dom);
        });
        parent.appendChild(_prop_value);
        dom.inline._x_reloaded = function(neue) {
            neue._x_reloaded = dom.inline._x_reloaded;
            delete dom.inline._x_reloaded;
            dom.inline = neue;
        } 
    }
    if (dom.block)
    {
        row = row + 1; 
        dom.block.style.gridRow = row;
        if (is_array_element)
        {
            dom.block.classList.add("array-element");
        }
        dom.block.classList.add("block-editor");
        parent.appendChild(dom.block);
        if (dom.inline)
        {
            var expand_me = expanded || dom.pre_expand;
            if (!expand_me)
                dom.block.classList.add("collapsed");
            _prop_value.classList.add("click-to-expand");
            _prop_value.addEventListener("click", function() {
                dom.block.classList.toggle("collapsed");
            });
            if (dom.inline._x_is_preview)
                _prop_value.classList.add("preview");
            // reload the preview if content changes
            dom.block._x_reloaded = function(neue) {
                neue._x_reloaded = dom.block._x_reloaded;
                delete dom.block._x_reloaded;
                dom.block = neue;
            }
            dom.block._x_changed = function() {
                update_label();
                if (dom.inline._x_reload) {
                    dom.inline = dom.inline._x_reload();
                }
            }
            dom.inline._x_changed = function() {
                update_label();
                if (dom.inline._x_reload) { dom.inline = dom.inline._x_reload(); }
                if (dom.block._x_reload) { dom.block = dom.block._x_reload(); }
            }
        }
    }
    else
    {
        dom.inline._x_changed = function() {
            update_label();
        };
    }
    row = row + 1;
    return row;
}

function build_properties(objdesc)
{
    var _properties = document.createElement('x-properties');
    var row = 1;
    var insert_fn = function(typename) {
        var type = resolve_type(typename);
        if (type.ExpandedFields)
        {
            for (var i=0;i<type.ExpandedFields.length;i++)
            {
                var f = type.ExpandedFields[i];
                if (!f.ShowInEditor)
                    continue;
                row = create_property(_properties, row, {
                    data: objdesc.data,
                    field: f,
                    datafield: f.Name,
                    parent: objdesc
                }); 
            }
        }
    }
    insert_fn(objdesc.type);
    return _properties;
}

function build_block_entry(objdesc)
{
    var _entry = document.createElement('x-inline-entry'); 
    var inline = build_properties(objdesc);
    _entry.appendChild(inline);
    _entry._x_changed = function() {
        console.log("object changed!");
    };
    return _entry;  
} 

function build_full_entry(objdesc, on_new_path, editor_func)
{
    var _entry = document.createElement('x-entry'); 
    var _path = document.createElement('x-path');
    var _type_text = document.createTextNode("@" + resolve_type(objdesc.type).PrettyName + " ");
    var _path_text = document.createTextNode(objdesc.path !== undefined ? objdesc.path : "<anonymous>");
    if (objdesc.path !== undefined && objdesc.path.indexOf("guid/") == 0)
        _path_text.textContent = "<guid>";
    _path.appendChild(_type_text);
    _path.appendChild(_path_text);
    _entry.appendChild(_path);
    if (editor_func != null) {
        _entry.appendChild(editor_func(plugin_config(), objdesc));
    } else {
        _entry.appendChild(build_properties(objdesc));
    }
    _path.addEventListener("click", function() {
        dialogs.prompt("Enter new path", objdesc.path, function (p) {
            if (p != null)
            {
                var old = Data[objdesc.path];
                if (old !== undefined) {
                    delete Data[objdesc.path];
                    Data[p] = old;
                    old._path = p;
                }
                if (objdesc.data._type == undefined) {
                    console.log("Inserting type on object.");
                    objdesc.data._type = objdesc.type;
                }
                objdesc.path = p;
                _path_text.textContent = p;
                if (on_new_path)
                    on_new_path(p);
                
                on_inline_changed(_path);
                on_change();
                reload_browser();
            }
        });
    });

    var pp = PluginObjectPostProcess[objdesc.type];
    if (pp !== undefined) {
        _entry = pp(_entry, objdesc);
    }
    return _entry;
}  

function build_root_entry(path, editor_func)
{
    return reload_wrapped(function() { return build_full_entry(
        {
            path: path,
            type: Data[path]._type,
            data: Data[path]
        },
        null,
        editor_func,
    )});
}

var project_root = null;

function plugin_config()
{
    return {
        project_root: project_root,
        types: UserTypes,
        data: Data,
        build_root_entry: build_root_entry,
        build_full_entry: function(x,y,z) { return reload_wrapped(function() { return build_full_entry(x, y, z); }); },
        electron: {
            dialogs: dialogs,
            popups: popups
        }
    }
}

ipcRenderer.on('edit-pointer-reply', (event, arg) => {
    if (nextEditPointer) {
        nextEditPointer(arg);
    }
});

function show_unset_values(show)
{
    if (!show)
    {
        if (unsetHidden.sheet.length > 0)
            unsetHidden.sheet.removeRule(0);
        unsetHidden.sheet.insertRule(".no-value { display: none; }", 0);
    }
    else
    {
        unsetHidden.sheet.removeRule(0);
    }
}

var unsetHidden = document.createElement('style');
document.head.appendChild(unsetHidden);
ipcRenderer.on('show-unset', (event, arg) => {
    show_unset_values(arg);
});

function activateTab(tab)
{
    var tabs = document.getElementById('tabs');
    var cn = tabs.childNodes;
    for (var x=0;x<cn.length;x++)
    {
        cn[x].classList.remove('active');
        if (cn[x] != tab)
            cn[x]._x_page.style.display = "none";
        else
        {
            cn[x]._x_page.style.display = "block";
            if (cn[x]._x_page._x_on_activate)
                cn[x]._x_page._x_on_activate();
            cn[x].classList.add('active');
        }
    }
}

function add_tab(title, page, on_close)
{
    var tab = document.createElement('x-tab');
    var lbl = document.createElement('x-tab-label');
    lbl.appendChild(document.createTextNode(title));
    tab.appendChild(lbl);
    document.getElementById('tabs').appendChild(tab);
    tab._x_page = page;
    tab.addEventListener('click', function() {
        activateTab(tab);
    })
    if (on_close)
    {
        var close = document.createElement('x-tab-close');
        close.appendChild(document.createTextNode("[X]"));
        tab.appendChild(close);
        tab._x_close = function() {
            if (on_close == null || on_close()) {
                tab.parentNode.removeChild(tab);
                page.parentNode.removeChild(page);
            };
            var els = document.getElementById('tabs').childNodes;            
            activateTab(els[els.length-1]);
        };
        close.addEventListener('click', function() {
            setTimeout(tab._x_close, 0);
        })
    }
    activateTab(tab);
    return tab;
}

function add_page(title, make, on_close)
{
    var page = document.createElement('x-page');
    document.getElementById('content').appendChild(page);
    make(page);    
    return add_tab(title, page, on_close);
}

ipcRenderer.on('close-object', function() {
    var els = document.getElementById('tabs').childNodes;
    for (var i=1;i<els.length;i++) {
        if (els[i].classList.contains('active')) {
            els[i]._x_close();
            break;
        }
    }
});

function goto_anchor(anchor)
{
    location.hash = '#';
    var els = document.getElementsByName(anchor);
    els.forEach(node => {
        while (node) {
            if (node.classList)
                node.classList.remove('collapsed');
            node = node.parentNode;
        }
    });
    location.hash = '#' + anchor;
}

function open_editor(path, editor, anchor)
{
    console.log("opening editor", path, editor, anchor);
    if (!Data.hasOwnProperty(path))
        return;        
    if (!Editing.hasOwnProperty(path))
    {        
        Editing[path] = add_page(path, function(page) {
            page.appendChild(build_root_entry(path, editor));
        }, function() {
            delete Editing[path];
            return true;
        });
        if (anchor) {
            goto_anchor(anchor);
        }
    } else {
        activateTab(Editing[path]);
        if (anchor) {
            goto_anchor(anchor);
        }
    }
}

var UserTypes = {};
var Data = {};
var Plugins = [];
var PluginObjectPostProcess = {};
var PluginFieldPostProcess = {};
var PluginFieldCustom = {};
var PluginBuildObject = {};
var Editing = {};
var Configuration = {};
var FileSet = {};
var Revision = "unknown";

ipcRenderer.on('choose-menu', function(event, data) {
    console.log("picked ", data);
    if (data.command == "plugin-edit") {
        open_editor(data.path, Plugins[data.plugin].object_editors[data.editor].editor);
    } else if (data.command == "edit") {
        open_editor(data.path);
    } else if (data.command == "move") {
        popups.ask_file(Data, function(npath) {
            Data[data.path]._file = npath;
            reload_browser();            
        });        
    } else if (data.command == "delete") {
        dialogs.confirm("Really delete " + data.path + "?", function(ok) {
            if (ok) {
                delete Data[data.path];
                reload_browser();
            }
        });
    }
});

ipcRenderer.on('save', function(event) {
    document.getElementById('dummy').focus();
    setTimeout(function() {
        var root = Configuration.root;
        if (Configuration.data["data-root"] !== undefined) {
            // untag those that remain.
            for (var k in Data) {
                FileSet[Data[k]._file] = false;
            }
            for (var k in FileSet) {
                if (FileSet[k]) {
                    fs.unlinkSync(path.join(root, Configuration.data["data-root"], k));
                    FileSet[k] = false;
                }
            }
            datawriter.write(path.join(root, Configuration.data["data-root"]), UserTypes, Data, undefined, PluginBuildObject);
            for (var k in Data) {
                FileSet[Data[k]._file] = true;
            }
        }
        if (Configuration.data["data-bundle"] !== undefined) {
            var data = {
                revision: Revision,
                data: Data
            };
            var pth = path.join(root, Configuration.data["data-bundle"]);
            fs.writeFileSync(pth, JSON.stringify(data, null, 10), "utf-8");
            console.log("Wrote bundle to ", pth, "with revision", data.revision);
        }
        if (Configuration.data["game-export"] !== undefined) {
            console.log("Writing game export bundle to", Configuration.data["game-export"]);
            datawriter.write(root, UserTypes, Data, Configuration.data["game-export"]);
        }
        document.title = Configuration.data["title"];
        event.sender.send("saved");
    }, 50);
});

ipcRenderer.send('request-configuration');

function dig_with_type(obj, def_type, cb)
{
    if (typeof obj === 'string')
    {
        return;
    }
    var tn = obj["_type"] || def_type;
    var t = resolve_type(tn);
    if (t === undefined)
    {
        console.log("Encountered object with unkown type ", obj, tn, def_type);
        return;
    }
    cb(obj, tn);    
    for (var i in t.ExpandedFields)
    {
        var field_name = t.ExpandedFields[i].Name;
        if (!obj.hasOwnProperty(field_name))
        {
            continue;
        }
        var value = obj[field_name];
        if (value === undefined)
            continue;
        var sub_type = t.ExpandedFields[i].Type;
        if (t.ExpandedFields[i].Array)
        {
            for (var j=0;j<value.length;j++)
            {
                dig_with_type(value[j], sub_type, cb);
            }
        }
        dig_with_type(value, sub_type, cb);
    }
}

ipcRenderer.on('configuration', function(evt, config) {
    var js = config.data["gen-js"];
    var plugins = config.data["plugins"];
    var root = config.root;
    project_root = root;
    Configuration = config;
    document.title = config.data["title"];
    for (var i in js) {
        var p = path.join(root, js[i]);
        console.log("Loading types from", p);
        var mod = require(p);
        for (var x in mod.Types) {
            UserTypes[x] = mod.Types[x];
        }
    }
    var id_counter = 0;

    for (var xx in UserTypes)
    {
        var flds = UserTypes[xx].Fields;
        if (flds) {
            for (var i=0;i<flds.length;i++)
                flds[i].Id = ++id_counter;
        }
    }

    for (var xx in UserTypes)
    {
        (function (x) {
            var all = [];
            var add = function(tn) {
                if (UserTypes[tn].Parent !== undefined)
                    add(UserTypes[tn].Parent);
                all = all.concat(UserTypes[tn].Fields);
            };
            if (UserTypes[x].Fields !== undefined)
            {
                add(x);
                UserTypes[x].ExpandedFields = all;
            }            
        })(xx);
        // assign unique ids for use as keys later.
    }
    var data_manglers = [];
    for (var i in plugins) {
        var p = path.join(root, plugins[i]);
        console.log("Loading plugin from", p);
        var plugin = require(p);        
        var pd = plugin.initialize(config, plugin_config());
        plugin.install_on_page();
        Plugins.push(pd);
        if (pd.data_mangler)
            data_manglers.push(pd.data_mangler);
        for (var x in pd.field_post_process) {
            PluginFieldPostProcess[x] = pd.field_post_process[x];
        }
        for (var x in pd.object_post_process) {
            PluginObjectPostProcess[x] = pd.object_post_process[x];
        }
        for (var x in pd.field_custom) {
            PluginFieldCustom[x] = pd.field_custom[x];
        }
        for (var x in pd.object_build) {
            PluginBuildObject[x] = pd.object_build[x];
        }
    }    
    if (config.data["data-root-legacy"] !== undefined) {
        dataloader.load_tree_legacy(path.join(root, config.data["data-root-legacy"]), Data, FileSet, UserTypes);
    } else {
        if (config.data["data-root"] !== undefined) {
            dataloader.load_tree(path.join(root, config.data["data-root"]), Data, FileSet);
        }
    }

    // clear out objects of unrecognized types
    for (var pt in Data)
    {
        var t = Data[pt]._type;
        if (t !== undefined && UserTypes[t] === undefined)
        {
            console.log("Clearing object at path ", pt, " because it has unrecognized type", t);
            delete Data[pt];
        }
    }


    if (config.data["data-bundle"] !== undefined) {
        var tmp = JSON.parse(fs.readFileSync(path.join(root, config.data["data-bundle"]), "utf-8"));
        for (var x in tmp.data) {
            Data[x] = tmp.data[x];
        }
        Revision = tmp.revision;
        console.log("Loaded data bundle from revision ", Revision);
    }
    data_manglers.forEach(x => {
        x(Data, dig_with_type);
    });
    add_page("Index", function(page) {
        var browser = databrowser.create(page, UserTypes, Data, Plugins, plugin_config(), function preview(d) {    
            if (d.hasOwnProperty('name'))
                return d['name'];
            return '';
        }, function(path, anchor) {
            open_editor(path, null, anchor);
        });
        reload_browser = function() { browser._x_reload(); }
    });
    if (process.env["EDIT_OBJ"])
        open_editor(process.env["EDIT_OBJ"]);
});

ipcRenderer.on("new-object", function() {
    var fn = function(type, file) {
        dialogs.prompt("Enter object path", "new/instance", function(path) {
            if (path) {
                Data[path] = {
                    _path: path,
                    _type: type,
                    _file: file
                };
                reload_browser();
                open_editor(path);
            }
        });
    };
    popups.ask_type(UserTypes, null, function(type) {
        popups.ask_file(Data, function(which) {
            if (which) {
                fn(type, which);
            }
        });
    });
});



/*
open_editor("game");

*/
/*
datawriter.write(UserTypes, Data);
=======
*/

//popups.ask_type(UserTypes, null, function(which) { console.log("selected ", which); });
//popups.ask_instance(UserTypes, Data, "item", function(which) { console.log("selected ", which); });

//document.body.appendChild(build_root_entry("manor/music-room/play-instrument"));
//document.body.appendChild(build_root_entry("procitems/trinkets/rune-necklace"));

