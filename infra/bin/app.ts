#!/usr/bin/env node
import 'source-map-support/register';
import * as cdk from 'aws-cdk-lib';
import { BillingLedgerStack } from '../lib/billing-ledger-stack';

const app = new cdk.App();

new BillingLedgerStack(app, 'BillingLedger', {
  env: {
    account: process.env.CDK_DEFAULT_ACCOUNT,
    region:  process.env.CDK_DEFAULT_REGION ?? 'us-east-1',
  },
  description:
    'BillingLedger production stack: VPC, RDS, Redis, ECS Fargate, SNS/SQS with DLQs',
});
