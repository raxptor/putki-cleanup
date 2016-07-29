#pragma once

#include <putki/builder/typereg.h>

namespace putki
{
	namespace signature
	{
		// Compute signature for an object
		typedef char buffer[64];
		const char *object(type_handler_i* th, instance_t obj, buffer buffer);
		const char *resource(const char* data, size_t size, buffer buffer);
	}
}
