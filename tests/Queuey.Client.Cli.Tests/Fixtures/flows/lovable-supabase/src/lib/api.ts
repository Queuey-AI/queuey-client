import axios from 'axios';

// A call from the browser to a server, not a route this repository serves.
export const createCheckout = (items: string[]) => axios.post('/api/checkout', { items });
