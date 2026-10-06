import Stripe from 'stripe';
import { NextResponse } from 'next/server';

const stripe = new Stripe(process.env.STRIPE_SECRET_KEY!);

export async function POST(req: Request) {
  const body = await req.text();
  const signature = req.headers.get('stripe-signature')!;

  let event: Stripe.Event;
  try {
    event = stripe.webhooks.constructEvent(body, signature, process.env.STRIPE_WEBHOOK_SECRET!);
  } catch {
    return NextResponse.json({ error: 'invalid signature' }, { status: 400 });
  }

  switch (event.type) {
    case 'checkout.session.completed':
      break;
    case 'customer.subscription.deleted':
      break;
  }

  return NextResponse.json({ received: true });
}
