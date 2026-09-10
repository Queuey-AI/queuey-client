import express from "express";
import pg from "pg";

const app = express();
app.use(express.json()); // parses every body before handlers run
const db = new pg.Pool();

app.post("/webhooks/supplier", async (req, res) => {
  const { shipmentId, status } = req.body;
  await db.query("update shipments set status = $2 where id = $1", [shipmentId, status]);
  res.sendStatus(200);
});

app.listen(5000);
