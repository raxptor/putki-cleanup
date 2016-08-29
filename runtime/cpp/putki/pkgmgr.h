#pragma once

#include <cstdint>
#include <stddef.h>
#include "types.h"

namespace putki
{
	namespace pkgmgr
	{
		struct loaded_package;
		struct resolve_status;

		struct resource_data
		{
			const char* data;
			size_t size;
			intptr_t internal;
		};
		
		// will call back and send beg/end/target to 0 when done, then expects everything to be loaded after that.
		typedef bool(*load_external_file_fn)(int file_index, const char *path, size_t beg, size_t end, void *target);

		// look at the first bytes and say if valid and how big the header is.
		bool get_header_info(char *beg, char *end, size_t *total_header_size, size_t *total_data_size);

		// parse from buffer, takes ownership.
		// if opt_out is passed in, it will be filled with resolve stauts.
		loaded_package * parse(char *header, char *data, load_external_file_fn ext_loader, resolve_status *opt_out);
		void free_on_release(loaded_package *);
		void release(loaded_package *);

		// must be resolved & done.
		void register_for_liveupdate(loaded_package *);

		resolve_status *alloc_resolve_status();
		void free_resolve_status(resolve_status *);

		// returns number of unresolved pointers remaining.
		int resolve_pointers_with(loaded_package *target, resolve_status *s, loaded_package *aux);

		// resolev from package.
		instance_t resolve(loaded_package *, const char *path);
		const char *path_in_package_slot(loaded_package *, unsigned int slot, bool only_if_content);
		int num_unresolved_slots(loaded_package *);
		int next_unresolved_slot(loaded_package *p, int start);

		// load resource by path
		bool load_resource(loaded_package* p, const char* path, resource_data* res);

		// load by id.
		bool load_resource(resource_id id, resource_data* res, loaded_package* extra);

		void free_resource(resource_data* res);
	}
}
