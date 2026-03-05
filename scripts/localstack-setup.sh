#!/bin/bash
set -e

echo "==> Setting up LocalStack SNS/SQS resources..."

REGION="us-east-1"
ACCOUNT="000000000000"
BASE_URL="http://localhost:4566"

# SNS Topics
INVOICE_TOPIC_ARN=$(awslocal sns create-topic --name billing-invoice-events --query TopicArn --output text)
PAYMENT_TOPIC_ARN=$(awslocal sns create-topic --name payments-payment-events --query TopicArn --output text)

echo "Created SNS topics: $INVOICE_TOPIC_ARN | $PAYMENT_TOPIC_ARN"

# SQS Queues + DLQs
# Payments Worker queue (consumes PaymentReceived, PaymentConfirmed)
PAYMENTS_DLQ_URL=$(awslocal sqs create-queue --queue-name payments-worker-dlq --query QueueUrl --output text)
PAYMENTS_DLQ_ARN=$(awslocal sqs get-queue-attributes --queue-url $PAYMENTS_DLQ_URL --attribute-names QueueArn --query Attributes.QueueArn --output text)
PAYMENTS_QUEUE_URL=$(awslocal sqs create-queue \
  --queue-name payments-worker-queue \
  --attributes RedrivePolicy="{\"deadLetterTargetArn\":\"$PAYMENTS_DLQ_ARN\",\"maxReceiveCount\":3}" \
  --query QueueUrl --output text)
PAYMENTS_QUEUE_ARN=$(awslocal sqs get-queue-attributes --queue-url $PAYMENTS_QUEUE_URL --attribute-names QueueArn --query Attributes.QueueArn --output text)

# Billing Worker queue (consumes PaymentConfirmed -> marks Invoice Paid)
BILLING_DLQ_URL=$(awslocal sqs create-queue --queue-name billing-worker-dlq --query QueueUrl --output text)
BILLING_DLQ_ARN=$(awslocal sqs get-queue-attributes --queue-url $BILLING_DLQ_URL --attribute-names QueueArn --query Attributes.QueueArn --output text)
BILLING_QUEUE_URL=$(awslocal sqs create-queue \
  --queue-name billing-worker-queue \
  --attributes RedrivePolicy="{\"deadLetterTargetArn\":\"$BILLING_DLQ_ARN\",\"maxReceiveCount\":3}" \
  --query QueueUrl --output text)
BILLING_QUEUE_ARN=$(awslocal sqs get-queue-attributes --queue-url $BILLING_QUEUE_URL --attribute-names QueueArn --query Attributes.QueueArn --output text)

# Ledger Worker queue (consumes InvoicePaid, InvoiceIssued)
LEDGER_DLQ_URL=$(awslocal sqs create-queue --queue-name ledger-worker-dlq --query QueueUrl --output text)
LEDGER_DLQ_ARN=$(awslocal sqs get-queue-attributes --queue-url $LEDGER_DLQ_URL --attribute-names QueueArn --query Attributes.QueueArn --output text)
LEDGER_QUEUE_URL=$(awslocal sqs create-queue \
  --queue-name ledger-worker-queue \
  --attributes RedrivePolicy="{\"deadLetterTargetArn\":\"$LEDGER_DLQ_ARN\",\"maxReceiveCount\":3}" \
  --query QueueUrl --output text)
LEDGER_QUEUE_ARN=$(awslocal sqs get-queue-attributes --queue-url $LEDGER_QUEUE_URL --attribute-names QueueArn --query Attributes.QueueArn --output text)

# SNS -> SQS subscriptions (fanout)
awslocal sns subscribe --topic-arn $INVOICE_TOPIC_ARN --protocol sqs --notification-endpoint $BILLING_QUEUE_ARN
awslocal sns subscribe --topic-arn $INVOICE_TOPIC_ARN --protocol sqs --notification-endpoint $LEDGER_QUEUE_ARN
awslocal sns subscribe --topic-arn $PAYMENT_TOPIC_ARN --protocol sqs --notification-endpoint $PAYMENTS_QUEUE_ARN
awslocal sns subscribe --topic-arn $PAYMENT_TOPIC_ARN --protocol sqs --notification-endpoint $BILLING_QUEUE_ARN

echo "==> LocalStack setup complete."
