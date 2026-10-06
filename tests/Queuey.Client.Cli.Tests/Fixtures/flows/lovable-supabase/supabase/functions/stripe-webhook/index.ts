import Stripe from 'https://esm.sh/stripe@14.25.0?target=deno';
import { createClient } from 'https://esm.sh/@supabase/supabase-js@2';

const stripe = new Stripe(Deno.env.get('STRIPE_SECRET_KEY')!, { apiVersion: '2024-06-20' });
const cryptoProvider = Stripe.createSubtleCryptoProvider();

Deno.serve(async (req) => {
  const signature = req.headers.get('Stripe-Signature');
  const body = await req.text();

  let event;
  try {
    event = await stripe.webhooks.constructEventAsync(
      body, signature!, Deno.env.get('STRIPE_WEBHOOK_SIGNING_SECRET')!, undefined, cryptoProvider);
  } catch (err) {
    return new Response(err.message, { status: 400 });
  }

  const supabase = createClient(Deno.env.get('SUPABASE_URL')!, Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!);
  if (event.type === 'checkout.session.completed') {
    await supabase.from('orders').upsert({ stripe_event_id: event.id, status: 'paid' }, { onConflict: 'stripe_event_id' });
  }

  return new Response(JSON.stringify({ received: true }), { status: 200 });
});
