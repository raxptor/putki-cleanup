#include "objstore.h"

#include <putki/sys/files.h>
#include <putki/builder/db.h>
#include <putki/builder/log.h>
#include <putki/builder/parse.h>
#include <putki/builder/write.h>
#include <putki/builder/typereg.h>

#include <string>
#include <map>
#include <vector>

namespace putki
{
	namespace objstore
	{
		struct file_entry
		{
			std::string path;
		};

		struct object_entry
		{
			file_entry* file;
			std::string path;
			std::string signature;

			type_handler_i* th;
			parse::node* node;
		};

		typedef std::map<std::string, object_entry> ObjMap;

		struct data
		{
			db::data* db;
			std::string root;
			std::vector<file_entry*> files;
			ObjMap objects;
		};

		namespace
		{
			struct make_unresolved : public load_resolver_i
			{
				db::data* db;
				void resolve_pointer(instance_t *ptr, const char *path)
				{
					*ptr = (instance_t)db::create_unresolved_pointer(db, path);
				}
			};
		}

		void fetch(const char* path, fetch_result* result)
		{

		}

		void fetch_free(fetch_result* result)
		{
			
		}

		void examine_file(const char *fullname, const char *name, void *userptr)
		{
			data* d = (data *)userptr;
			std::string fn(name);
			size_t pos = fn.find_last_of('.');
			if (pos == std::string::npos)
			{
				return;
			}
			if (strcmp(fn.substr(pos, fn.size() - pos).c_str(), ".json"))
			{
				return;
			}
			std::string objname = fn.substr(0, pos);

			parse::data *pd = parse::parse(fullname);
			if (!pd)
			{
				APP_INFO("Parse error in file [" << fullname << "]");
				return;
			}

			file_entry* fe = new file_entry();
			fe->path = fullname;
			d->files.push_back(fe);

			parse::node *root = parse::get_root(pd);
			std::string objtype = parse::get_value_string(parse::get_object_item(root, "type"));
			type_handler_i *th = typereg_get_handler(objtype.c_str());
			if (th)
			{
				instance_t obj = th->alloc();
				make_unresolved mur;
				mur.db = d->db;
				th->fill_from_parsed(parse::get_object_item(root, "data"), obj, &mur);
				db::insert(d->db, objname.c_str(), th, obj);
				object_entry e;
				e.file = fe;
				e.path = objname;
				char buf[64];
				e.signature = db::signature(d->db, objname.c_str(), buf);
				e.node = parse::get_object_item(root, "data");
				e.th = th;
				d->objects.insert(std::make_pair(objname, e));
			}
			else
			{
				APP_WARNING("Unrecognized type [" << objtype << "]");
			}
			
			parse::node *aux = parse::get_object_item(root, "aux");
			if (aux)
			{
				for (int i = 0;; i++)
				{
					parse::node *aux_obj = parse::get_array_item(aux, i);
					if (!aux_obj)
					{
						break;
					}
					std::string objtype = parse::get_value_string(parse::get_object_item(aux_obj, "type"));
					std::string auxpath = std::string(objname) + parse::get_value_string(parse::get_object_item(aux_obj, "ref"));
					type_handler_i *th = typereg_get_handler(objtype.c_str());
					if (th)
					{
						instance_t obj = th->alloc();
						make_unresolved mur;
						mur.db = d->db;
						th->fill_from_parsed(parse::get_object_item(aux_obj, "data"), obj, &mur);
						db::insert(d->db, auxpath.c_str(), th, obj);
						object_entry e;
						e.file = fe;
						e.path = auxpath;
						e.node = parse::get_object_item(aux_obj, "data");
						e.th = th;
						char buf[64];
						e.signature = db::signature(d->db, auxpath.c_str(), buf);
						d->objects.insert(std::make_pair(auxpath, e));
					}
				}
			}
		}
		
		data* open(const char *root_path)
		{
			data* d = new data();
			d->db = db::create();
			d->root = root_path;
			sys::search_tree((d->root + "/objs").c_str(), examine_file, d);

			db::free(d->db);
			d->db = db::create();

			ObjMap::iterator i = d->objects.begin();
			while (i != d->objects.end())
			{
				APP_DEBUG("[" << i->first << "] sig=" << i->second.signature);
				++i;
			}
			return d;
		}
		
		void free(data *d)
		{
			db::free(d->db);
			delete d;
		}

		bool fetch_object(data* d, const char* path, fetch_result* result)
		{
			ObjMap::iterator i = d->objects.find(path);
			if (i == d->objects.end())
			{
				return false;
			}
			else
			{
				result->node = i->second.node;
				result->th = i->second.th;
				return true;
			}
		}

		bool query_object(data* d, const char *path, object_info* result)
		{
			ObjMap::iterator i = d->objects.find(path);
			if (i == d->objects.end())
			{
				return false;
			}
			else
			{
				result->signature = i->second.signature;
				result->th = i->second.th;
				return true;
			}
		}

		bool store_object(data* d, const char *path, db::data* ref_source, type_handler_i* th, instance_t obj, const char *signature)
		{
			std::string out_path(d->root);
			out_path.append("/objs/");
			out_path.append(path);
			out_path.append(".json");
			putki::sstream ts;
			write::write_object_into_stream(ts, ref_source, th, obj);
			sys::mk_dir_for_path(out_path.c_str());
			if (!sys::write_file(out_path.c_str(), ts.str().c_str(), (unsigned long)ts.str().size()))
			{
				return false;
			}

			std::string fn(path);
			fn.append(".json");
			examine_file(out_path.c_str(), fn.c_str(), d);
			return true;
		}

		void fetch_object_free(data* d, fetch_result* result)
		{

		}
		
		std::string signature(const char *path)
		{
			return "none";
		}
	}
}
