---
title: Send a message
source: Content/reference/send-message.htm
---

# Send a message

Send a message to a channel or to one person with `POST /api/v1/messages`.

See [Interacting with messages](../talk/messages.md) for the client view.

## Usage example

```
import client

result = client.send_message({
    "type": "stream",
    "to": "general",
    "content": "Hello & welcome to the <team>!",
})
print(result["id"])
```

## Arguments

| Argument | Type | Description |
|---|---|---|
| `type` | string | Either `stream` or `direct`. |
| `to` | string | The channel name or the recipient's email. |

**Note:** A message can hold up to 10,000 characters.

id

: The ID of the new message.
