Deno.serve(async (req) => {
  if (req.headers.get('x-webhook-secret') !== Deno.env.get('ORDERS_WEBHOOK_SECRET')) {
    return new Response('unauthorized', { status: 401 });
  }

  const payload = await req.json();
  if (payload.type === 'UPDATE' && payload.old_record?.status !== payload.record.status) {
    await notifyCustomer(payload.record);
  }
  if (payload.type === 'INSERT') {
    await sendReceipt(payload.record);
  }

  return new Response('ok');
});

async function notifyCustomer(order: { id: number }) {
  console.log('status changed', order.id);
}

async function sendReceipt(order: { id: number }) {
  console.log('receipt', order.id);
}
