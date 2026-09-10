# Signup service

FastAPI service on the platform team's Kubernetes cluster (3 replicas, no
persistent volumes — pods are cattle). Writes users to Postgres and notifies
the CRM partner over HTTPS. Egress leaves through the corporate proxy, which
resets long-lived connections and periodically blackholes traffic for a few
minutes; we have lost notifications and had to reconcile by hand.
