#ifndef SEMANTIC_KNOWLEDGE_H
#define SEMANTIC_KNOWLEDGE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
  #define SK_CALL __cdecl
#else
  #define SK_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define SK_ABI_VERSION 1u
#define SK_OK 0
#define SK_ERROR 1

typedef intptr_t sk_store_t;

typedef struct sk_buffer {
    uint8_t *data;
    size_t length;
} sk_buffer;

uint32_t SK_CALL sk_abi_version(void);
int SK_CALL sk_version(uint8_t *buffer, size_t capacity);
int SK_CALL sk_get_last_error(uint8_t *buffer, size_t capacity);
void SK_CALL sk_buffer_free(sk_buffer *buffer);

int SK_CALL sk_store_open_sqlite_json(const uint8_t *json, size_t length, sk_store_t *handle);
int SK_CALL sk_store_close(sk_store_t handle);
int SK_CALL sk_store_initialize_json(sk_store_t handle, sk_buffer *output);
int SK_CALL sk_store_reset(sk_store_t handle);

int SK_CALL sk_knowledge_base_get_or_create_json(sk_store_t handle, const uint8_t *json, size_t length, sk_buffer *output);
int SK_CALL sk_collection_get_or_create_json(sk_store_t handle, const uint8_t *json, size_t length, sk_buffer *output);
int SK_CALL sk_schema_ensure_json(sk_store_t handle, const uint8_t *json, size_t length, sk_buffer *output);
int SK_CALL sk_document_upsert_json(sk_store_t handle, const uint8_t *json, size_t length, sk_buffer *output);
int SK_CALL sk_document_get_json(sk_store_t handle, const uint8_t *json, size_t length, sk_buffer *output);
int SK_CALL sk_search_json(sk_store_t handle, const uint8_t *json, size_t length, sk_buffer *output);

#ifdef __cplusplus
}
#endif

#endif
