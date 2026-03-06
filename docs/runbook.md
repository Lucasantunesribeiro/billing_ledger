# BillingLedger — Production Runbook

> **Audience**: On-call engineers responsible for deploying and operating the BillingLedger system on AWS.
> All commands assume `aws` CLI ≥ 2.x, `cdk` ≥ 2.180, and a profile with `AdministratorAccess` unless otherwise noted.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [First-Time Bootstrap & Deploy](#2-first-time-bootstrap--deploy)
3. [Deploying a New Version](#3-deploying-a-new-version)
4. [Updating Secrets After Deploy](#4-updating-secrets-after-deploy)
5. [Running EF Core Migrations in Production](#5-running-ef-core-migrations-in-production)
6. [Monitoring — CloudWatch Logs](#6-monitoring--cloudwatch-logs)
7. [Alerting — CloudWatch Alarms](#7-alerting--cloudwatch-alarms)
8. [DLQ Investigation & Redrive](#8-dlq-investigation--redrive)
9. [Rolling Back a Deploy](#9-rolling-back-a-deploy)
10. [Common Failure Scenarios](#10-common-failure-scenarios)

---

## 1. Prerequisites

```bash
# Install CDK globally (if not present)
npm install -g aws-cdk

# Install infra dependencies
cd infra/
npm install

# Verify AWS identity
aws sts get-caller-identity
```

Set environment variables once per shell session:

```bash
export AWS_PROFILE=billing-ledger-deploy     # IAM profile with deploy permissions
export AWS_DEFAULT_REGION=us-east-1
export ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
```

---

## 2. First-Time Bootstrap & Deploy

### 2.1 Bootstrap the CDK toolkit

CDK requires a staging bucket and IAM roles in the target account/region (one-time per account+region):

```bash
cd infra/
cdk bootstrap aws://${ACCOUNT_ID}/${AWS_DEFAULT_REGION}
```

### 2.2 Build the TypeScript stack

```bash
npm run build          # compiles lib/ and bin/ to dist/
```

### 2.3 Preview the changes

```bash
cdk diff               # shows what will be created/modified/deleted
```

### 2.4 Deploy the stack

```bash
cdk deploy BillingLedger \
  --parameters ImageTag=latest \
  --require-approval never
```

> The deploy takes ~15 minutes on first run (VPC, RDS, ElastiCache, ECS services).

### 2.5 Save stack outputs

```bash
aws cloudformation describe-stacks \
  --stack-name BillingLedger \
  --query 'Stacks[0].Outputs' \
  --output table
```

Key outputs to note:

| Output | Description |
|--------|-------------|
| `AlbDnsName` | Public entry point — configure your DNS CNAME here |
| `EcrBillingApi` | ECR URI for billing-api |
| `EcrPaymentsWorker` | ECR URI for payments-worker |
| `EcrLedgerWorker` | ECR URI for ledger-worker |
| `RdsEndpoint` | Private RDS hostname |
| `DbSecretArn` | Secrets Manager ARN for DB credentials |

---

## 3. Deploying a New Version

### 3.1 Build and push Docker images

```bash
# Authenticate Docker to ECR
aws ecr get-login-password | \
  docker login --username AWS --password-stdin \
  ${ACCOUNT_ID}.dkr.ecr.${AWS_DEFAULT_REGION}.amazonaws.com

TAG=$(git rev-parse --short HEAD)    # use git SHA for traceability

# Build from repo root (Dockerfiles use multi-stage builds)
docker build -f src/BillingLedger.Billing.Api/Dockerfile      -t billing-api:${TAG}      .
docker build -f src/BillingLedger.Payments.Worker/Dockerfile  -t payments-worker:${TAG}  .
docker build -f src/BillingLedger.Ledger.Worker/Dockerfile    -t ledger-worker:${TAG}    .

# Tag and push
for svc in billing-api payments-worker ledger-worker; do
  REPO="${ACCOUNT_ID}.dkr.ecr.${AWS_DEFAULT_REGION}.amazonaws.com/billing-ledger/${svc}"
  docker tag ${svc}:${TAG}   ${REPO}:${TAG}
  docker tag ${svc}:${TAG}   ${REPO}:latest
  docker push ${REPO}:${TAG}
  docker push ${REPO}:latest
done
```

### 3.2 Deploy the new tag

```bash
cd infra/
cdk deploy BillingLedger \
  --parameters ImageTag=${TAG} \
  --require-approval never
```

CDK updates the ECS task definitions; ECS performs a rolling replacement with zero downtime.

### 3.3 Verify health

```bash
ALB=$(aws cloudformation describe-stacks \
  --stack-name BillingLedger \
  --query 'Stacks[0].Outputs[?OutputKey==`AlbDnsName`].OutputValue' \
  --output text)

curl http://${ALB}/health         # expects: {"status":"Healthy"}
```

---

## 4. Updating Secrets After Deploy

The CDK creates placeholder secrets for JWT key and webhook secret. **Update them before serving real traffic:**

```bash
# Generate a strong JWT signing key (≥ 32 chars for HS256)
JWT_KEY=$(openssl rand -base64 48)
aws secretsmanager put-secret-value \
  --secret-id billing-ledger/jwt/signing-key \
  --secret-string "${JWT_KEY}"

# Set the webhook secret (share this with your payment provider)
WEBHOOK_SECRET=$(openssl rand -hex 32)
aws secretsmanager put-secret-value \
  --secret-id billing-ledger/payments/webhook-secret \
  --secret-string "${WEBHOOK_SECRET}"
```

After updating secrets, **force a new ECS deployment** so containers pick up the new values:

```bash
aws ecs update-service \
  --cluster billing-ledger \
  --service BillingLedgerBillingApiService \
  --force-new-deployment
```

---

## 5. Running EF Core Migrations in Production

Migrations must be applied before (or immediately after) deploying a new version.

### Option A — One-off ECS task (recommended)

```bash
# Get the task definition ARN
TASK_DEF=$(aws ecs describe-task-definition \
  --task-definition BillingLedgerBillingApiTaskDef \
  --query 'taskDefinition.taskDefinitionArn' --output text)

# Get private subnet and security group IDs from the stack
SUBNET=$(aws cloudformation describe-stacks \
  --stack-name BillingLedger \
  --query 'Stacks[0].Outputs' --output json | \
  jq -r '.[] | select(.OutputKey=="PrivateSubnet0Id") | .OutputValue')

# Run migration as a one-off task (override the entrypoint)
aws ecs run-task \
  --cluster billing-ledger \
  --task-definition ${TASK_DEF} \
  --launch-type FARGATE \
  --overrides '{"containerOverrides":[{"name":"billing-api","command":["dotnet","BillingLedger.Billing.Api.dll","--migrate"]}]}' \
  --network-configuration "awsvpcConfiguration={subnets=[${SUBNET}],securityGroups=[sg-xxx],assignPublicIp=DISABLED}"
```

> **Note**: The application must handle `--migrate` argument to run `Database.MigrateAsync()` then exit. Alternatively, apply migrations from a CI/CD step using the RDS endpoint + credentials from Secrets Manager.

### Option B — Local apply (via SSM port-forward)

```bash
# Open a tunnel to RDS via Session Manager (no bastion host needed)
aws ssm start-session \
  --target mi-xxxxx \           # SSM-managed instance in the VPC
  --document-name AWS-StartPortForwardingSessionToRemoteHost \
  --parameters "host=${RDS_HOST},portNumber=5432,localPortNumber=5432"

# Apply migrations locally (in another terminal)
cd /mnt/g/.programacao/billing_ledger
dotnet ef database update --project src/BillingLedger.Billing.Api \
  --connection "Host=localhost;Port=5432;Database=billing_ledger;Username=billing_admin;Password=${DB_PASSWORD}"
```

---

## 6. Monitoring — CloudWatch Logs

All ECS containers ship logs to CloudWatch Logs with a 1-month retention policy.

### Log groups

| Service | Log Group |
|---------|-----------|
| Billing.Api | `/ecs/billing-ledger/billing-api` |
| Payments.Worker | `/ecs/billing-ledger/payments-worker` |
| Ledger.Worker | `/ecs/billing-ledger/ledger-worker` |

### Tail live logs (last 5 minutes)

```bash
# Billing.Api
aws logs tail /ecs/billing-ledger/billing-api \
  --follow --since 5m --format short

# Payments.Worker
aws logs tail /ecs/billing-ledger/payments-worker \
  --follow --since 5m --format short
```

### Search for errors

```bash
aws logs filter-log-events \
  --log-group-name /ecs/billing-ledger/billing-api \
  --filter-pattern '"level":"Error"' \
  --start-time $(($(date +%s) - 3600))000 \
  --query 'events[].message' \
  --output text
```

### Search by CorrelationId

Every request carries a `CorrelationId` (propagated via `X-Correlation-Id` header and Serilog enricher):

```bash
aws logs filter-log-events \
  --log-group-name /ecs/billing-ledger/billing-api \
  --filter-pattern '"CorrelationId":"<paste-id-here>"' \
  --query 'events[].message' \
  --output text
```

---

## 7. Alerting — CloudWatch Alarms

Four DLQ alarms + one ALB 5xx alarm are created by the CDK stack.

### View alarm states

```bash
aws cloudwatch describe-alarms \
  --alarm-name-prefix BillingLedger \
  --query 'MetricAlarms[].{Name:AlarmName,State:StateValue,Reason:StateReason}' \
  --output table
```

### Wire alarms to SNS notification (post-deploy one-time setup)

```bash
# Create an ops notification topic
OPS_TOPIC=$(aws sns create-topic --name billing-ledger-ops-alerts --output text --query TopicArn)

# Subscribe your email
aws sns subscribe --topic-arn ${OPS_TOPIC} \
  --protocol email --notification-endpoint ops-team@example.com

# Attach to each DLQ alarm
for ALARM in InvoiceIssuedDlqAlarm InvoicePaidDlqAlarm PaymentReceivedDlqAlarm PaymentConfirmedDlqAlarm Alb5xxAlarm; do
  aws cloudwatch put-metric-alarm \
    --alarm-name "BillingLedger${ALARM}" \
    --alarm-actions ${OPS_TOPIC} \
    --ok-actions ${OPS_TOPIC}
done
```

---

## 8. DLQ Investigation & Redrive

When a DLQ alarm fires, messages have failed 3 retries. Follow these steps:

### 8.1 Inspect DLQ message count

```bash
for Q in bl-invoice-issued-dlq bl-invoice-paid-dlq bl-payment-received-dlq bl-payment-confirmed-dlq; do
  COUNT=$(aws sqs get-queue-attributes \
    --queue-url https://sqs.${AWS_DEFAULT_REGION}.amazonaws.com/${ACCOUNT_ID}/${Q} \
    --attribute-names ApproximateNumberOfMessages \
    --query 'Attributes.ApproximateNumberOfMessages' --output text)
  echo "${Q}: ${COUNT} messages"
done
```

### 8.2 Read a message from the DLQ (without deleting)

```bash
DLQ_URL="https://sqs.${AWS_DEFAULT_REGION}.amazonaws.com/${ACCOUNT_ID}/bl-payment-received-dlq"

aws sqs receive-message \
  --queue-url ${DLQ_URL} \
  --max-number-of-messages 1 \
  --attribute-names All \
  --message-attribute-names All \
  --query 'Messages[0].Body' \
  --output text | jq .
```

Key fields to inspect: `EventId`, `InvoiceId`, `CorrelationId`. Use the CorrelationId to find the full trace in CloudWatch Logs (see §6).

### 8.3 Fix the root cause

Common causes:

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| `23505` unique constraint | Idempotency bug | Check EventId column; idempotency should handle this silently |
| `NpgsqlException: connection refused` | RDS not reachable | Check security group rules, RDS status |
| `JsonException` | Malformed message | Fix publisher; delete poison messages manually |
| `401 / 403` from downstream | Auth config | Verify Secrets Manager secrets are current |

### 8.4 Redrive messages (after root cause is fixed)

**Via AWS Console (quickest):**
1. Open SQS → select the DLQ → **Start DLQ Redrive**
2. Set destination queue to the primary queue
3. Set max messages per batch → **Start**

**Via CLI:**

```bash
PRIMARY_URL="https://sqs.${AWS_DEFAULT_REGION}.amazonaws.com/${ACCOUNT_ID}/bl-payment-received"
DLQ_URL="https://sqs.${AWS_DEFAULT_REGION}.amazonaws.com/${ACCOUNT_ID}/bl-payment-received-dlq"

# Get DLQ ARN
DLQ_ARN=$(aws sqs get-queue-attributes \
  --queue-url ${DLQ_URL} \
  --attribute-names QueueArn \
  --query 'Attributes.QueueArn' --output text)

# Start the managed redrive
aws sqs start-message-move-task \
  --source-arn ${DLQ_ARN} \
  --destination-arn $(aws sqs get-queue-attributes \
      --queue-url ${PRIMARY_URL} \
      --attribute-names QueueArn \
      --query 'Attributes.QueueArn' --output text) \
  --max-number-of-messages-per-second 10
```

### 8.5 Delete poison messages permanently (last resort)

If a message cannot be processed (e.g., corrupted payload from a decommissioned publisher), delete it:

```bash
# Receive the message to get its ReceiptHandle
MSG=$(aws sqs receive-message --queue-url ${DLQ_URL} --max-number-of-messages 1)
RECEIPT=$(echo $MSG | jq -r '.Messages[0].ReceiptHandle')

# Log the body before deleting for audit trail
echo $MSG | jq '.Messages[0].Body' >> ~/dlq-deleted-$(date +%Y%m%d).log

# Delete
aws sqs delete-message --queue-url ${DLQ_URL} --receipt-handle ${RECEIPT}
```

---

## 9. Rolling Back a Deploy

ECS keeps previous task definition revisions. Rollback is instant:

```bash
# Find the previous revision
aws ecs describe-task-definition \
  --task-definition BillingLedgerBillingApiTaskDef \
  --query 'taskDefinition.revision'
# e.g., current is 5, so rollback to 4

# Update service to previous revision
aws ecs update-service \
  --cluster billing-ledger \
  --service BillingLedgerBillingApiService \
  --task-definition BillingLedgerBillingApiTaskDef:4

# Watch the deployment stabilise
aws ecs wait services-stable \
  --cluster billing-ledger \
  --services BillingLedgerBillingApiService
echo "Rolled back successfully"
```

> If the deploy also included a database migration, rollback requires a separate migration rollback plan (EF Core `dotnet ef database update <PreviousMigration>`).

---

## 10. Common Failure Scenarios

### Billing.Api containers keep restarting (exit code 1)

```bash
# Check the most recent stopped task's exit reason
aws ecs describe-tasks \
  --cluster billing-ledger \
  --tasks $(aws ecs list-tasks --cluster billing-ledger \
              --family BillingLedgerBillingApiTaskDef \
              --desired-status STOPPED \
              --query 'taskArns[0]' --output text) \
  --query 'tasks[0].containers[0].{Reason:reason,ExitCode:exitCode}'
```

Most common causes:
- `Jwt__SigningKey` secret not updated (container fails JWT middleware init) → see §4
- `ConnectionStrings__Postgres` unreachable (security group or RDS down)
- Missing environment variable → check task definition in ECS Console

### Workers not processing messages

```bash
# Check SQS queue depths (primary queues — not DLQs)
for Q in bl-invoice-issued bl-invoice-paid bl-payment-received bl-payment-confirmed; do
  aws sqs get-queue-attributes \
    --queue-url https://sqs.${AWS_DEFAULT_REGION}.amazonaws.com/${ACCOUNT_ID}/${Q} \
    --attribute-names ApproximateNumberOfMessages ApproximateNumberOfMessagesNotVisible \
    --query "Attributes" | jq -r '"'"${Q}"': visible=\(.ApproximateNumberOfMessages) inflight=\(.ApproximateNumberOfMessagesNotVisible)"'
done
```

High `ApproximateNumberOfMessagesNotVisible` (inflight) with no processing = worker crashed while consuming. Check worker logs.

### Invoice SAGA stuck (invoice stays Issued, never goes to Paid)

Trace the CorrelationId through all three log groups:

```bash
CID="<correlation-id-from-customer-report>"

for LG in billing-api payments-worker ledger-worker; do
  echo "=== /ecs/billing-ledger/${LG} ==="
  aws logs filter-log-events \
    --log-group-name /ecs/billing-ledger/${LG} \
    --filter-pattern "\"${CID}\"" \
    --query 'events[].message' --output text
done
```

Typical causes: PaymentConfirmedV1 landed in DLQ (check `bl-payment-confirmed-dlq`), or `PaymentReceivedConsumer` failed to publish `PaymentConfirmedV1` to SNS.
