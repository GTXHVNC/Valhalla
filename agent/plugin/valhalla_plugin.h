#ifndef VALHALLA_PLUGIN_H
#define VALHALLA_PLUGIN_H

#include <stdint.h>

#ifdef _WIN32
#define VALHALLA_API __declspec(dllexport)
#define VALHALLA_CALL __cdecl
#else
#define VALHALLA_API
#define VALHALLA_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef int32_t (VALHALLA_CALL *valhalla_emit)(const uint8_t *event, uint32_t event_len,
                                   const uint8_t *payload, uint32_t payload_len);

/* Host/plugin event names understood by the agent transport. Plugins may use
   any other UTF-8 event name for application-specific messages. */
#define VALHALLA_EVENT_FILE_SEND_BEGIN "file.send.begin"
#define VALHALLA_EVENT_FILE_SEND_CHUNK "file.send.chunk"
#define VALHALLA_EVENT_FILE_SEND_END   "file.send.end"

/* file.send.begin payload: UTF-8 "transferId|fileName|size|sha256" */
/* file.send.chunk payload: UTF-8 "transferId|offset|" followed by raw bytes */
/* file.send.end payload: UTF-8 "transferId" */

VALHALLA_API int32_t VALHALLA_CALL PluginOnLoad(const uint8_t *host, uint32_t host_len, valhalla_emit emit);
VALHALLA_API int32_t VALHALLA_CALL PluginOnEvent(const uint8_t *event, uint32_t event_len,
                                     const uint8_t *payload, uint32_t payload_len);
VALHALLA_API void VALHALLA_CALL PluginOnUnload(void);

#ifdef __cplusplus
}
#endif

#endif
