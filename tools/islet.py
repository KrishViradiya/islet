"""
Islet Live Activities - dependency-free Python client.

Watch your model train from the Dynamic Island:

    from islet import Activity

    with Activity("Training ResNet-18") as act:
        for epoch in range(epochs):
            loss = train_one_epoch()
            act.update(progress=(epoch + 1) / epochs, subtitle=f"epoch {epoch + 1}/{epochs} - loss {loss:.4f}")
    # leaving the block marks it green; an exception marks it red with the error message

If Islet isn't running, every call silently does nothing, so it never breaks your script.
Run this file directly for a demo:  python islet.py
"""
import json
import urllib.request

BASE = "http://localhost:47823"


def _send(method, path, body=None):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=1.5) as resp:
            raw = resp.read()
            return json.loads(raw) if raw else None
    except Exception:
        return None


class Activity:
    def __init__(self, title, subtitle=None, id=None):
        self.id = id
        self.title = title
        self.subtitle = subtitle

    def start(self):
        body = {"title": self.title, "state": "running"}
        if self.id:
            body["id"] = self.id
        if self.subtitle:
            body["subtitle"] = self.subtitle
        reply = _send("POST", "/activities", body)
        if reply:
            self.id = reply.get("id")
        return self

    def update(self, progress=None, subtitle=None, title=None):
        """progress: 0..1 (or None to leave unchanged)."""
        if not self.id:
            return
        body = {}
        if progress is not None:
            body["progress"] = float(progress)
        if subtitle is not None:
            body["subtitle"] = str(subtitle)
        if title is not None:
            body["title"] = str(title)
        _send("POST", f"/activities/{self.id}", body)

    def finish(self, ok=True, subtitle=None):
        if not self.id:
            return
        body = {"state": "success" if ok else "error"}
        if subtitle:
            body["subtitle"] = subtitle
        _send("POST", f"/activities/{self.id}", body)

    def __enter__(self):
        return self.start()

    def __exit__(self, exc_type, exc, tb):
        if exc_type is None:
            self.finish(ok=True)
        else:
            self.finish(ok=False, subtitle=f"{exc_type.__name__}: {exc}")
        return False  # never swallow the user's exception


if __name__ == "__main__":
    import math
    import random
    import time

    epochs = 20
    with Activity("Training demo model", "warming up") as act:
        for epoch in range(epochs):
            time.sleep(0.4)
            loss = 2.5 * math.exp(-epoch / 6) + random.random() * 0.05
            act.update(progress=(epoch + 1) / epochs, subtitle=f"epoch {epoch + 1}/{epochs} - loss {loss:.4f}")
    print("done - check the island")
