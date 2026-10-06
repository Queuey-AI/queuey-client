const express = require('express');
const webhooks = require('./routes/webhooks');

const app = express();

// The webhook router reads its own raw body, so it is mounted before the JSON parser.
app.use('/webhooks', webhooks);
app.use(express.json());

app.get('/health', (req, res) => res.send('ok'));

const PORT = process.env.PORT || 4242;
app.listen(PORT, () => console.log(`listening on ${PORT}`));
