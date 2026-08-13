#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include "semantic_knowledge.h"

#ifdef _WIN32
#include <windows.h>
#define LIB_HANDLE HMODULE
#define OPEN_LIB(path) LoadLibraryA(path)
#define LOAD_SYM(lib, name) GetProcAddress(lib, name)
#define CLOSE_LIB(lib) FreeLibrary(lib)
#else
#include <dlfcn.h>
#define LIB_HANDLE void*
#define OPEN_LIB(path) dlopen(path, RTLD_NOW | RTLD_LOCAL)
#define LOAD_SYM(lib, name) dlsym(lib, name)
#define CLOSE_LIB(lib) dlclose(lib)
#endif

typedef uint32_t (SK_CALL *abi_version_fn)(void);
typedef int (SK_CALL *version_fn)(uint8_t*, size_t);
typedef int (SK_CALL *get_error_fn)(uint8_t*, size_t);
typedef int (SK_CALL *open_sqlite_fn)(const uint8_t*, size_t, sk_store_t*);
typedef int (SK_CALL *close_store_fn)(sk_store_t);

static void print_last_error(get_error_fn get_error) {
    int required = get_error(NULL, 0);
    if (required <= 0) return;
    uint8_t *buffer = (uint8_t*)calloc((size_t)required, 1);
    if (!buffer) return;
    get_error(buffer, (size_t)required);
    fprintf(stderr, "SemanticKnowledge native error: %s\n", (const char*)buffer);
    free(buffer);
}

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: native-smoke <SemanticKnowledge.Native library>\n");
        return 2;
    }

    LIB_HANDLE lib = OPEN_LIB(argv[1]);
    if (!lib) {
        fprintf(stderr, "failed to load native library\n");
        return 3;
    }

    abi_version_fn abi_version = (abi_version_fn)LOAD_SYM(lib, "sk_abi_version");
    version_fn version = (version_fn)LOAD_SYM(lib, "sk_version");
    get_error_fn get_error = (get_error_fn)LOAD_SYM(lib, "sk_get_last_error");
    open_sqlite_fn open_sqlite = (open_sqlite_fn)LOAD_SYM(lib, "sk_store_open_sqlite_json");
    close_store_fn close_store = (close_store_fn)LOAD_SYM(lib, "sk_store_close");

    if (!abi_version || !version || !get_error || !open_sqlite || !close_store) {
        fprintf(stderr, "required ABI symbol missing\n");
        CLOSE_LIB(lib);
        return 4;
    }

    if (abi_version() != SK_ABI_VERSION) {
        fprintf(stderr, "ABI version mismatch\n");
        CLOSE_LIB(lib);
        return 5;
    }

    uint8_t version_buffer[64] = {0};
    if (version(version_buffer, sizeof(version_buffer)) <= 0 || version_buffer[0] == 0) {
        fprintf(stderr, "version export failed\n");
        CLOSE_LIB(lib);
        return 6;
    }

    const char *json = "{\"databasePath\":\"native-smoke.db\",\"databaseVersion\":1,\"rebuildable\":true}";
    sk_store_t store = 0;
    if (open_sqlite((const uint8_t*)json, strlen(json), &store) != SK_OK || store == 0) {
        print_last_error(get_error);
        CLOSE_LIB(lib);
        return 7;
    }

    if (close_store(store) != SK_OK) {
        print_last_error(get_error);
        CLOSE_LIB(lib);
        return 8;
    }

    remove("native-smoke.db");
    remove("native-smoke.db-wal");
    remove("native-smoke.db-shm");
    CLOSE_LIB(lib);
    printf("SemanticKnowledge C ABI smoke passed (%s).\n", version_buffer);
    return 0;
}
