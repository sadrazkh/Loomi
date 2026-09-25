"""Loomi API v1 with requests: start a chat, wait for its picture, download it, then continue the chat.
Run: LOOMI_TOKEN=lm_... python loomi.py"""
import os
import time

import requests

BASE = "https://loomi.example.com"
session = requests.Session()
session.headers["Authorization"] = "Bearer " + os.environ["LOOMI_TOKEN"]


def call(method, path, **kwargs):
    """One request. Waits out a 429 as Retry-After asks, and raises with the error code on any other failure."""
    while True:
        response = session.request(method, BASE + path, timeout=90, **kwargs)
        if response.status_code != 429:
            break
        time.sleep(int(response.headers.get("Retry-After", "60")))
    if not response.ok:
        raise RuntimeError(f"HTTP {response.status_code} {response.text}")
    return response


def wait_for(message_id):
    """Long-polls until the message is finished; each call holds for up to a minute."""
    while True:
        try:
            reply = call("GET", f"/api/v1/messages/{message_id}/wait", params={"timeout": 60}).json()
        except (requests.ConnectionError, requests.Timeout):
            time.sleep(5)  # a dropped connection loses nothing: the message keeps going on the server
            continue
        if reply["done"]:
            return reply["message"]
        print("still", reply["message"]["stage"])


def save(message, path):
    """Downloads a finished message's picture, or says why there is none."""
    if message["status"] != "completed":
        raise RuntimeError(f"no picture: {message['status']} {message['error'] or message['text']}")
    with open(path, "wb") as file:
        file.write(call("GET", message["imageUrl"]).content)
    print("saved", path)


# 1. Start a chat with its first message.
started = call("POST", "/api/v1/chats", json={"prompt": "a lighthouse at dusk, film grain"}).json()
# 2. Wait for the answer and download the picture.
save(wait_for(started["message"]["id"]), "lighthouse.png")
# 3. The next message in the same chat, then wait and download again.
sent = call("POST", f"/api/v1/chats/{started['chat']['id']}/messages", json={"prompt": "the same scene in winter"}).json()
save(wait_for(sent["message"]["id"]), "lighthouse-winter.png")
