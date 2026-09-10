from fastapi import FastAPI
from sqlalchemy import create_engine, text
from tenacity import retry, stop_after_attempt, wait_exponential
import httpx, os

app = FastAPI()
engine = create_engine(os.environ["DATABASE_URL"])


@retry(stop=stop_after_attempt(5), wait=wait_exponential(min=1, max=30))
def notify_crm(user_id: str, email: str) -> None:
    # In-memory retry only: if the pod dies mid-retry the notification is lost.
    r = httpx.post("https://crm.example/api/contacts", json={"id": user_id, "email": email}, timeout=10)
    r.raise_for_status()


@app.post("/signup")
def signup(email: str):
    with engine.begin() as conn:
        user_id = conn.execute(text("insert into users(email) values (:e) returning id"), {"e": email}).scalar_one()
    notify_crm(str(user_id), email)
    return {"id": user_id}
