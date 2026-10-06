#ifndef EINHERJAR_PLUGIN_H
#define EINHERJAR_PLUGIN_H

#include <stdint.h>

#ifdef _WIN32
#define EINHERJAR_API __declspec(dllexport)
#define EINHERJAR_CALL __cdecl
#else
#define EINHERJAR_API
#define EINHERJAR_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef int32_t (EINHERJAR_CALL *einherjar_emit)(const uint8_t *event, uint32_t event_len,
                                   const uint8_t *payload, uint32_t payload_len);

/* Host/plugin event names understood by the agent transport. Plugins may use
   any other UTF-8 event name for application-specific messages. */
#define EINHERJAR_EVENT_FILE_SEND_BEGIN "file.send.begin"
#define EINHERJAR_EVENT_FILE_SEND_CHUNK "file.send.chunk"
#define EINHERJAR_EVENT_FILE_SEND_END   "file.send.end"

/* file.send.begin payload: UTF-8 "transferId|fileName|size|sha256" */
/* file.send.chunk payload: UTF-8 "transferId|offset|" followed by raw bytes */
/* file.send.end payload: UTF-8 "transferId" */

EINHERJAR_API int32_t EINHERJAR_CALL PluginOnLoad(const uint8_t *host, uint32_t host_len, einherjar_emit emit);
EINHERJAR_API int32_t EINHERJAR_CALL PluginOnEvent(const uint8_t *event, uint32_t event_len,
                                     const uint8_t *payload, uint32_t payload_len);
EINHERJAR_API void EINHERJAR_CALL PluginOnUnload(void);

#ifdef __cplusplus
}
#endif

#endif
