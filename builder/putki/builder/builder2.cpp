#include <putki/builder/typereg.h>
#include <putki/builder/parse.h>
#include <putki/builder/db.h>
#include <putki/builder/build-db.h>
#include <putki/builder/ptr.h>
#include "builder2.h"

#include <map>
#include <set>
#include <queue>

namespace putki
{
	namespace builder2
	{
		struct loaded
		{
			type_handler_i* th;
			instance_t obj;
			std::string signature;
		};

		typedef std::multimap<int, handler_info> HandlerMapT;
		typedef std::map<std::string, loaded> LoadedT;
		typedef std::vector<const char*> AllocStringsT;

		struct to_build
		{
			std::string path;
			int domain;
		};

		struct data
		{
			config conf;
			db::data* output;
			HandlerMapT handlers;
			std::queue<to_build> to_build;
			std::set<std::string> has_added;
			LoadedT loaded_input;
			LoadedT loaded_temp;
			LoadedT loaded_built;
			std::vector<const char*> str_allocs;
		};
		
		struct build_info_internal
		{
			data* data;
			std::vector<ptr_raw> outputs;
			ptr_context* ptr_context;
		};

		data* create(config* conf)
		{
			data* d = new data();
			d->conf = *conf;
			d->output = db::create();
			return d;
		}

		void free(data *d)
		{
			db::free(d->output);
			delete d;
		}

		void add_handlers(data* d, const handler_info* begin, const handler_info* end)
		{
			for (const handler_info* i = begin; i != end; i++)
			{
				type_handler_i* th = typereg_get_handler(i->type_id);
				while (th)
				{
					d->handlers.insert(std::make_pair(th->id(), *i));
					th = th->parent_type();
				}
			}
		}

		std::string builder_name(data *d, int type_id)
		{
			std::string builder_name;
			std::pair<HandlerMapT::iterator, HandlerMapT::iterator> hs = d->handlers.equal_range(type_id);
			for (HandlerMapT::iterator i = hs.first; i != hs.second; i++)
			{
				if (!builder_name.empty())
					builder_name.append("&");
				builder_name.append(i->second.name);
			}
			return builder_name.empty() ? "default" : builder_name;
		}

		void create_build_output(struct putki::builder2::build_info const *info, struct putki::type_handler_i *th, char const *tag, struct putki::ptr_raw *ptr)
		{
			std::string actual(info->path);
			actual.append("-");
			actual.append(tag);

			RECORD_INFO(info->record, "Creating output object [" << actual << "] type=" << th->name());
			instance_t obj = th->alloc();
			ptr->path = _strdup(actual.c_str());
			ptr->has_resolved = true;
			ptr->obj = obj;
			ptr->th = th;
			ptr->user_data = 1;
			ptr->ctx = info->internal->ptr_context;

			info->internal->data->str_allocs.push_back(ptr->path);
			info->internal->outputs.push_back(*ptr);
		}
		
		std::string store_resource(const build_info* info, const char* tag, const char* data, size_t size)
		{
			std::string actual(info->path);
			actual.append("-");
			actual.append(tag);
			if (!objstore::store_resource(info->internal->data->conf.temp, actual.c_str(), data, size))
			{
				RECORD_ERROR(info->record, "Failed to store temp resource [" << actual << "] size=" << size);
				return "";
			}
			objstore::resource_info ri;
			if (!objstore::query_resource(info->internal->data->conf.temp, actual.c_str(), &ri))
			{
				RECORD_ERROR(info->record, "Failed to query stored resource [" << actual << "] size=" << size);
				return "";
			}
			return ri.signature;
		}

		bool fetch_resource(const build_info* info, const char* path, resource* resource)
		{
			objstore::resource_info ri;
			if (objstore::query_resource(info->internal->data->conf.temp, path, &ri))
			{
				if (objstore::fetch_resource(info->internal->data->conf.temp, path, ri.signature.c_str(), &resource->internal))
				{
					resource->signature = _strdup(ri.signature.c_str());
					resource->data = resource->internal.data;
					resource->size = resource->internal.size;
					build_db::add_external_resource_dependency(info->record, path, resource->signature);
					return true;
				}
				else
				{
					build_db::add_external_resource_dependency(info->record, path, "file-not-found");
					APP_WARNING("Thought i could fetch resource [" << path << "] from tmp but then i couldn't!");
					return false;
				}
			}
			if (objstore::query_resource(info->internal->data->conf.input, path, &ri))
			{
				if (objstore::fetch_resource(info->internal->data->conf.input, path, ri.signature.c_str(), &resource->internal))
				{
					resource->signature = _strdup(ri.signature.c_str());
					resource->data = resource->internal.data;
					resource->size = resource->internal.size;
					build_db::add_external_resource_dependency(info->record, path, resource->signature);
					return true;
				}
				else
				{
					APP_WARNING("Thought i could fetch resource [" << path << "] from input but then i couldn't!");
				}
			}
			build_db::add_external_resource_dependency(info->record, path, "file-not-found");
			return false;
		}
		
		void free_resource(resource* resource)
		{
			::free((void*)resource->signature);
			objstore::fetch_resource_free(&resource->internal);
		}

		struct find_runtime_deps : public depwalker_i
		{
			db::data* db;
			std::set<std::string> ptrs;
			build_db::record* record;

			bool pointer_pre(instance_t *ptr, const char *ptr_type_name)
			{
				if (!*ptr)
				{
					return false;
				}

				const char *path = db::pathof_including_unresolved(db, *ptr);
				if (!path)
				{
					RECORD_ERROR(record, "Post-build there was an unrecognized pointer!");
					return false;
				}

				ptrs.insert(path);
				RECORD_DEBUG(record, "dep:" << path);
				return false;
			}
		};

		void add_build_root(data *d, const char *path)
		{
			if (!d->has_added.count(path))
			{
				d->has_added.insert(path);

				to_build tb;
				tb.path = path;
				tb.domain = 0;
				d->to_build.push(tb);
			}
		}
		
		bool fetch_cached(data* d, const char* path, objstore::object_info* info, const char* bname, build_db::InputDepSigs& sigs)
		{
			build_db::record* find = build_db::find_cached(d->conf.build_db, path, info->signature.c_str(), bname, sigs);
			if (!find)
			{
				return false;
			}

			build_db::deplist* dl = build_db::inputdeps_get(find);
			int di = 0;
			while (true)
			{
				const char* dep = build_db::deplist_path(dl, di);
				if (!dep)
				{
					break;
				}
				if (!deplist_is_external_resource(dl, di))
				{
					objstore::object_info dep_info;
					if (objstore::query_object(d->conf.input, dep, &dep_info))
					{
						if (strcmp(dep_info.signature.c_str(), build_db::deplist_signature(dl, di)))
						{
							APP_DEBUG("fetch_cached: obj-dep check for [" << dep << "] => " << dep_info.signature << " (record had " << build_db::deplist_signature(dl, di) << ")");
							sigs.insert(std::make_pair(std::string(dep), dep_info.signature));
							return fetch_cached(d, path, info, bname, sigs);
						}
					}
					else if (objstore::query_object(d->conf.temp, dep, &dep_info))
					{
						if (strcmp(dep_info.signature.c_str(), build_db::deplist_signature(dl, di)))
						{
							APP_DEBUG("fetch_cached: obj-dep check for [" << dep << "] => " << dep_info.signature << " (record had " << build_db::deplist_signature(dl, di) << ")");
							sigs.insert(std::make_pair(std::string(dep), dep_info.signature));
							return fetch_cached(d, path, info, bname, sigs);
						}
					}
					else
					{
						APP_DEBUG("fetch_cached => unknown input object [" << dep << "]");
						return false;
					}
				}
				else
				{
					objstore::resource_info res_info;
					if (objstore::query_resource(d->conf.input, dep, &res_info))
					{
						if (strcmp(res_info.signature.c_str(), build_db::deplist_signature(dl, di)))
						{
							APP_DEBUG("fetch_cached: res-dep check for [" << dep << "] => " << res_info.signature << " (record had " << build_db::deplist_signature(dl, di) << ")");
							sigs.insert(std::make_pair(std::string(dep), res_info.signature));
							return fetch_cached(d, path, info, bname, sigs);
						}
					}
					else if (objstore::query_resource(d->conf.temp, dep, &res_info))
					{
						if (strcmp(res_info.signature.c_str(), build_db::deplist_signature(dl, di)))
						{
							APP_DEBUG("fetch_cached: res-dep check for [" << dep << "] => " << res_info.signature << " (record had " << build_db::deplist_signature(dl, di) << ")");
							sigs.insert(std::make_pair(std::string(dep), res_info.signature));
							return fetch_cached(d, path, info, bname, sigs);
						}
					}
					else if (!strcmp(build_db::deplist_signature(dl, di), "file-not-found"))
					{
						APP_DEBUG("fetch_cached: obj still not exists");
					}
					else
					{
						APP_DEBUG("fetch_cached => [" << dep << "] does not exist any longer.");
						return false;
					}
				}
				++di;
			}

			APP_DEBUG("fetch_cached: I have a match")
			build_db::InputDepSigs::iterator i = sigs.begin();
			while (i != sigs.end())
			{
				APP_DEBUG("fetch_cached:  filter[" << i->first << "] => [" << i->second << "]");
				++i;
			}

			int o = 0;
			while (true)
			{
				const char* out = build_db::enum_outputs(find, o);
				if (!out)
				{
					break;
				}
				const char* out_sig = get_output_signature(find, o);
				if (!objstore::uncache_object(d->conf.temp, d->conf.temp, out, out_sig))
				{
					// Is cleanup here actually needed? Probably not.
					APP_DEBUG("Could not uncache object " << out << " sig=" << out_sig);
					return false;
				}
				else
				{
					APP_DEBUG("Uncached tmp object " << out << " sig=" << build_db::get_signature(find));
				}
				++o;
			}

			if (!objstore::uncache_object(d->conf.built, d->conf.built, path, build_db::get_signature(find)))
			{
				APP_DEBUG("Could not uncache object " << path << " sig=" << build_db::get_signature(find));
				return false;
			}

			int p = 0;
			while (true)
			{
				const char* ptr = build_db::get_pointer(find, p++);
				if (!ptr)
				{
					break;
				}
				if (!d->has_added.count(ptr))
				{
					to_build p;
					p.path = ptr;
					p.domain = 0;
					objstore::object_info oi;
					if (objstore::query_object(d->conf.temp, ptr, &oi))
					{
						p.domain = 1;
					}

					d->to_build.push(p);
					d->has_added.insert(ptr);
				}
			}
			return true;
		}

		bool fetch_cached(data* d, const char* path, objstore::object_info* info, const char* bname)
		{
			build_db::InputDepSigs sigs;
			return fetch_cached(d, path, info, bname, sigs);
		}

		void fixup_pointers(data* d, type_handler_i* th, instance_t obj, ptr_context* context, const char* root_path)
		{
			ptr_query_result result;
			th->query_pointers(obj, &result, false, true);
			for (size_t i = 0; i < result.pointers.size(); i++)
			{
				ptr_raw *p = result.pointers[i];
				p->ctx = context;
				if (p->path == 0 || !p->path[0])
				{
					continue;
				}
				if (p->path[0] == '#')
				{
					std::string actual(root_path);
					size_t already = actual.find_last_of('#');
					if (already != std::string::npos)
					{
						actual.erase(actual.begin() + already, actual.end());
					}
					actual.append(p->path);
					p->path = _strdup(actual.c_str());
					d->str_allocs.push_back(p->path);
				}
				
				objstore::object_info info;
				if (objstore::query_object(d->conf.temp, p->path, &info))
				{
					p->user_data = 1;
				}
				else
				{
					p->user_data = 0;
				}
			}
		}
		
		struct ptr_ctx_data
		{
			data* d;
			std::set<const char*> visited;
		};

		void ptr_resolve_info(ptr_raw* ptr, objstore::object_info* info)
		{
			if (ptr->path == 0 || !ptr->path[0])
			{
				ptr->obj = 0;
				return;
			}

			ptr_ctx_data* pcd = (ptr_ctx_data*)ptr->ctx->user_data;
			data* d = pcd->d;
			objstore::data* store;
			LoadedT* cache;
			switch (ptr->user_data)
			{
			case 0:
				store = d->conf.input;
				cache = &d->loaded_input;
				break;
			case 1:
				store = d->conf.temp;
				cache = &d->loaded_temp;
				break;
			case 2:
				store = d->conf.built;
				cache = &d->loaded_built;
				break;
			default:
				APP_ERROR("Invalid ptr domain. I do not know from which store to get it.");
				ptr->obj = 0;
				break;
			}

			// TODO: Verify that pointers are compatible with what is actually being loaded.

			LoadedT::iterator i = cache->find(ptr->path);
			if (i != cache->end())
			{
				ptr->obj = i->second.obj;
				ptr->th = i->second.th;
				info->signature = i->second.signature;
				info->th = i->second.th;
				return;
			}
			
			if (!objstore::query_object(store, ptr->path, info))
			{
				APP_ERROR("Unable to resolve " << ptr->path);
				ptr->obj = 0;
				return;
			}

			objstore::fetch_obj_result result;
			if (!objstore::fetch_object(store, ptr->path, info->signature.c_str(), &result))
			{
				APP_ERROR("Unable to fetch " << ptr->path);
				ptr->obj = 0;
				return;
			}

			instance_t obj = info->th->alloc();
			info->th->fill_from_parsed(result.node, obj);
			fixup_pointers(d, info->th, obj, ptr->ctx, ptr->path);
			
			loaded l;
			l.obj = obj;
			l.th = info->th;
			char sig[64];
			l.signature = db::signature(info->th, obj, sig);

			cache->insert(std::make_pair(std::string(ptr->path), l));

			ptr->th = info->th;
			ptr->obj = obj;
		}

		void ptr_resolve(ptr_raw* ptr)
		{
			objstore::object_info info;
			ptr_resolve_info(ptr, &info);
		}

		void ptr_deref(const ptr_raw* ptr)
		{
			ptr_ctx_data* pcd = (ptr_ctx_data*) ptr->ctx->user_data;
			if (ptr->path != 0 && ptr->path[0] != 0)
			{
				pcd->visited.insert(ptr->path);
			}
		}

		void do_build(data *d, bool incremental)
		{
			ptr_ctx_data pcd;
			pcd.d = d;

			ptr_context pctx;
			pctx.user_data = (uintptr_t)&pcd;
			pctx.deref = ptr_deref;
			pctx.resolve = ptr_resolve;

			while (true)
			{
				if (d->to_build.empty())
				{
					break;
				}

				to_build next = d->to_build.front();
				d->to_build.pop();

				const char* path = next.path.c_str();

				ptr_raw source;
				source.ctx = &pctx;
				source.has_resolved = false;
				source.user_data = next.domain;
				source.path = path;

				APP_DEBUG("Processing object [" << next.path << "] domain=" << next.domain);

				objstore::object_info info;
				ptr_resolve_info(&source, &info);
				if (!source.obj)
				{
					continue;
				}

				std::string bname = builder_name(d, source.th->id());

				if (incremental && fetch_cached(d, path, &info, bname.c_str()))
				{
					APP_DEBUG("=> Got cached object, no build needed.");
					continue;
				}

				source.obj = info.th->clone(source.obj);

				build_info_internal bii;
				bii.data = d;
				bii.ptr_context = &pctx;

				build_info bi;
				bi.path = path;
				bi.build_config = d->conf.build_config;
				bi.type = info.th;
				bi.object = source.obj;
				bi.record = build_db::create_record(path, info.signature.c_str(), builder_name(d, info.th->id()).c_str());;
				bi.internal = &bii;

				pcd.visited.clear();

				bool has_error = false;
				std::pair<HandlerMapT::iterator, HandlerMapT::iterator> hs = d->handlers.equal_range(info.th->id());
				for (HandlerMapT::iterator i = hs.first; i != hs.second; i++)
				{
					bi.builder = i->second.name;
					bi.user_data = i->second.user_data;
					RECORD_INFO(bi.record, "=> Invoking builder " << bi.builder << "...");
					if (!i->second.fn(&bi))
					{
						RECORD_ERROR(bi.record, "Error occured when building with builder " << bi.builder);
						has_error = true;
						break;
					}
				}
				
				if (hs.first == hs.second)
				{
					RECORD_INFO(bi.record, "=> No processing needed.");
				}
				
				std::set<const char*> ignore;
				for (size_t i = 0; i != bii.outputs.size(); i++)
				{
					char buffer[64];
					const char* sig = db::signature(bii.outputs[i].th, bii.outputs[i].obj, buffer);
					objstore::store_object(d->conf.temp, bii.outputs[i].path, bii.outputs[i].th, bii.outputs[i].obj, sig);
					build_db::add_output(bi.record, bii.outputs[i].path, bname.c_str(), sig);

					loaded le;
					le.th = bii.outputs[i].th;
					le.obj = bii.outputs[i].obj;
					le.signature = sig;
					d->loaded_temp.insert(std::make_pair(std::string(bii.outputs[i].path), le));
					ignore.insert(bii.outputs[i].path);
				}

				std::set<const char*>::iterator deps = pcd.visited.begin();
				while (deps != pcd.visited.end())
				{
					if (ignore.find(*deps) != ignore.end())
					{
						++deps;
						continue;
					}
					LoadedT::iterator i = d->loaded_temp.find(*deps);
					if (i != d->loaded_temp.end())
					{
						build_db::add_input_dependency(bi.record, *deps, i->second.signature.c_str());
					}
					else
					{
						i = d->loaded_input.find(*deps);
						if (i != d->loaded_input.end())
						{
							build_db::add_input_dependency(bi.record, *deps, i->second.signature.c_str());
						}
						else
						{
							APP_ERROR("visited set contained entry " << *deps << " not in either input or temp!");
						}
					}
					++deps;
				}
			
				build_db::flush_log(bi.record);
				build_db::insert_metadata(bi.record, source.th, source.obj, source.path);
				build_db::commit_record(d->conf.build_db, bi.record);

				ptr_query_result ptrs;
				source.th->query_pointers(source.obj, &ptrs, true, true);

				// Add runtime dependencies.
				for (size_t i = 0; i < ptrs.pointers.size();i++)
				{
					ptr_raw* p = ptrs.pointers[i];
					if (p->path != 0 && p->path[0])
					{
						if (!d->has_added.count(p->path))
						{
							to_build tb;
							tb.path = p->path;
							tb.domain = (int)p->user_data;
							d->to_build.push(tb);
							d->has_added.insert(p->path);
						}
					}
				}

				objstore::store_object(d->conf.built, path, info.th, source.obj, build_db::get_signature(bi.record));
			}
		}
	}
}