import * as cdk                from 'aws-cdk-lib';
import { Construct }           from 'constructs';
import * as ec2                from 'aws-cdk-lib/aws-ec2';
import * as ecs                from 'aws-cdk-lib/aws-ecs';
import * as elbv2              from 'aws-cdk-lib/aws-elasticloadbalancingv2';
import * as rds                from 'aws-cdk-lib/aws-rds';
import * as elasticache        from 'aws-cdk-lib/aws-elasticache';
import * as ecr                from 'aws-cdk-lib/aws-ecr';
import * as secretsmanager     from 'aws-cdk-lib/aws-secretsmanager';
import * as sns                from 'aws-cdk-lib/aws-sns';
import * as sqs                from 'aws-cdk-lib/aws-sqs';
import * as subscriptions      from 'aws-cdk-lib/aws-sns-subscriptions';
import * as cloudwatch         from 'aws-cdk-lib/aws-cloudwatch';
import * as logs               from 'aws-cdk-lib/aws-logs';
import * as iam                from 'aws-cdk-lib/aws-iam';
import { Duration, RemovalPolicy } from 'aws-cdk-lib';

// ─── Helper: SQS queue + DLQ pair ────────────────────────────────────────────
interface QueuePair { queue: sqs.Queue; dlq: sqs.Queue; }

function makeQueue(scope: Construct, id: string, name: string): QueuePair {
  const dlq = new sqs.Queue(scope, `${id}Dlq`, {
    queueName:       `${name}-dlq`,
    retentionPeriod: Duration.days(14),   // keep failed messages 14 days for investigation
  });

  const queue = new sqs.Queue(scope, `${id}Queue`, {
    queueName:         name,
    visibilityTimeout: Duration.seconds(60),
    deadLetterQueue:   { queue: dlq, maxReceiveCount: 3 },  // 3 retries before DLQ
    retentionPeriod:   Duration.days(4),
  });

  return { queue, dlq };
}

// ─── Helper: DLQ depth CloudWatch Alarm ──────────────────────────────────────
function dlqAlarm(scope: Construct, id: string, dlq: sqs.Queue, label: string): cloudwatch.Alarm {
  return new cloudwatch.Alarm(scope, id, {
    metric: dlq.metricApproximateNumberOfMessagesVisible({
      period:    Duration.minutes(1),
      statistic: 'Maximum',
    }),
    threshold:           1,
    evaluationPeriods:   1,
    comparisonOperator:  cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
    alarmDescription:    `${label} DLQ has unprocessed messages — investigate immediately`,
    treatMissingData:    cloudwatch.TreatMissingData.NOT_BREACHING,
  });
}

// ─────────────────────────────────────────────────────────────────────────────

export class BillingLedgerStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    // ── Parameter: Docker image tag (supplied by CI/CD at deploy time) ─────────
    const imageTag = new cdk.CfnParameter(this, 'ImageTag', {
      type:        'String',
      default:     'latest',
      description: 'Docker image tag to deploy to all three ECS services',
    }).valueAsString;

    // ─────────────────────────────────────────────────────────────────────────
    // VPC  — 2 AZs, 1 NAT GW (cost-saving for portfolio; use 2 for HA in prod)
    // ─────────────────────────────────────────────────────────────────────────
    const vpc = new ec2.Vpc(this, 'Vpc', {
      maxAzs:      2,
      natGateways: 1,
      subnetConfiguration: [
        { name: 'public',  subnetType: ec2.SubnetType.PUBLIC,                cidrMask: 24 },
        { name: 'private', subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS,   cidrMask: 24 },
      ],
    });

    // ─────────────────────────────────────────────────────────────────────────
    // Security Groups
    // ─────────────────────────────────────────────────────────────────────────
    const albSg = new ec2.SecurityGroup(this, 'AlbSg', {
      vpc, description: 'Internet-facing ALB — allow HTTP from anywhere',
    });
    albSg.addIngressRule(ec2.Peer.anyIpv4(), ec2.Port.tcp(80),  'HTTP');
    albSg.addIngressRule(ec2.Peer.anyIpv4(), ec2.Port.tcp(443), 'HTTPS');

    const appSg = new ec2.SecurityGroup(this, 'AppSg', {
      vpc, description: 'ECS Fargate tasks — ingress from ALB only',
    });
    appSg.addIngressRule(albSg, ec2.Port.tcp(8080), 'ALB → Billing.Api');

    const rdsSg = new ec2.SecurityGroup(this, 'RdsSg', {
      vpc, description: 'RDS Postgres — ingress from ECS tasks only',
    });
    rdsSg.addIngressRule(appSg, ec2.Port.tcp(5432), 'ECS → Postgres');

    const redisSg = new ec2.SecurityGroup(this, 'RedisSg', {
      vpc, description: 'ElastiCache Redis — ingress from ECS tasks only',
    });
    redisSg.addIngressRule(appSg, ec2.Port.tcp(6379), 'ECS → Redis');

    // ─────────────────────────────────────────────────────────────────────────
    // Secrets Manager
    // Sensitive values are NEVER in code or environment variables in plaintext.
    // ECS task definitions reference secrets by ARN → injected at container start.
    // ─────────────────────────────────────────────────────────────────────────
    const dbSecret = new secretsmanager.Secret(this, 'DbSecret', {
      secretName: 'billing-ledger/rds/credentials',
      generateSecretString: {
        secretStringTemplate: JSON.stringify({ username: 'billing_admin' }),
        generateStringKey:    'password',
        excludePunctuation:   true,
        passwordLength:       32,
      },
      description: 'Auto-generated RDS PostgreSQL credentials (username + password)',
    });

    // NOTE: After first deploy, update these placeholder values via AWS Console or CLI:
    //   aws secretsmanager put-secret-value \
    //     --secret-id billing-ledger/jwt/signing-key \
    //     --secret-string "$(openssl rand -base64 48)"
    const jwtSecret = new secretsmanager.Secret(this, 'JwtSecret', {
      secretName:          'billing-ledger/jwt/signing-key',
      secretStringValue:   cdk.SecretValue.unsafePlainText('REPLACE_AFTER_FIRST_DEPLOY'),
      description:         'JWT HS256 signing key (min 32 chars) — update after deploy',
    });

    const webhookSecret = new secretsmanager.Secret(this, 'WebhookSecret', {
      secretName:          'billing-ledger/payments/webhook-secret',
      secretStringValue:   cdk.SecretValue.unsafePlainText('REPLACE_AFTER_FIRST_DEPLOY'),
      description:         'HMAC-SHA256 webhook signature secret — update after deploy',
    });

    // ─────────────────────────────────────────────────────────────────────────
    // RDS PostgreSQL 16 — t3.micro (portfolio sizing; use r7g.large for prod)
    // ─────────────────────────────────────────────────────────────────────────
    const db = new rds.DatabaseInstance(this, 'Postgres', {
      engine: rds.DatabaseInstanceEngine.postgres({
        version: rds.PostgresEngineVersion.VER_16,
      }),
      instanceType:     ec2.InstanceType.of(ec2.InstanceClass.T3, ec2.InstanceSize.MICRO),
      vpc,
      vpcSubnets:       { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups:   [rdsSg],
      credentials:      rds.Credentials.fromSecret(dbSecret),
      databaseName:     'billing_ledger',
      multiAz:          false,         // set true for production HA
      storageEncrypted: true,
      backupRetention:  Duration.days(7),
      removalPolicy:    RemovalPolicy.SNAPSHOT,
      deletionProtection: false,       // set true in production
    });

    // ─────────────────────────────────────────────────────────────────────────
    // ElastiCache Redis (single node — t3.micro for portfolio)
    // ─────────────────────────────────────────────────────────────────────────
    const redisSubnetGroup = new elasticache.CfnSubnetGroup(this, 'RedisSubnets', {
      description:          'Private subnets for BillingLedger Redis',
      subnetIds:            vpc.privateSubnets.map(s => s.subnetId),
      cacheSubnetGroupName: 'billing-ledger-redis',
    });

    const redis = new elasticache.CfnCacheCluster(this, 'Redis', {
      engine:                  'redis',
      cacheNodeType:           'cache.t3.micro',
      numCacheNodes:           1,
      cacheSubnetGroupName:    redisSubnetGroup.ref,
      vpcSecurityGroupIds:     [redisSg.securityGroupId],
      autoMinorVersionUpgrade: true,
    });
    redis.addDependency(redisSubnetGroup);

    // ─────────────────────────────────────────────────────────────────────────
    // ECR Repositories — CI/CD pipeline pushes images; CDK deploys by tag
    // ─────────────────────────────────────────────────────────────────────────
    const billingApiRepo      = new ecr.Repository(this, 'BillingApiRepo',      { repositoryName: 'billing-ledger/billing-api',      removalPolicy: RemovalPolicy.RETAIN, lifecycleRules: [{ maxImageCount: 10 }] });
    const paymentsWorkerRepo  = new ecr.Repository(this, 'PaymentsWorkerRepo',  { repositoryName: 'billing-ledger/payments-worker',  removalPolicy: RemovalPolicy.RETAIN, lifecycleRules: [{ maxImageCount: 10 }] });
    const ledgerWorkerRepo    = new ecr.Repository(this, 'LedgerWorkerRepo',    { repositoryName: 'billing-ledger/ledger-worker',    removalPolicy: RemovalPolicy.RETAIN, lifecycleRules: [{ maxImageCount: 10 }] });

    // ─────────────────────────────────────────────────────────────────────────
    // SNS Topics — one per message type for clean routing boundaries
    //
    //   bl-invoice-issued   ← Billing.Api  → LedgerWorker (InvoiceIssuedConsumer)
    //   bl-invoice-paid     ← Billing.Api  → LedgerWorker (InvoicePaidConsumer)
    //   bl-payment-received ← Billing.Api  → PaymentsWorker (PaymentReceivedConsumer)
    //   bl-payment-confirmed← PaymentsWorker→ Billing.Api (PaymentConfirmedConsumer)
    // ─────────────────────────────────────────────────────────────────────────
    const invoiceIssuedTopic    = new sns.Topic(this, 'InvoiceIssuedTopic',    { topicName: 'bl-invoice-issued',    displayName: 'Invoice Issued Events' });
    const invoicePaidTopic      = new sns.Topic(this, 'InvoicePaidTopic',      { topicName: 'bl-invoice-paid',      displayName: 'Invoice Paid Events' });
    const paymentReceivedTopic  = new sns.Topic(this, 'PaymentReceivedTopic',  { topicName: 'bl-payment-received',  displayName: 'Payment Received Events' });
    const paymentConfirmedTopic = new sns.Topic(this, 'PaymentConfirmedTopic', { topicName: 'bl-payment-confirmed', displayName: 'Payment Confirmed Events' });

    // ─────────────────────────────────────────────────────────────────────────
    // SQS Queues + DLQs (maxReceiveCount: 3 → 3 retries before message lands in DLQ)
    // ─────────────────────────────────────────────────────────────────────────
    const { queue: invoiceIssuedQ,    dlq: invoiceIssuedDlq    } = makeQueue(this, 'InvoiceIssued',    'bl-invoice-issued');
    const { queue: invoicePaidQ,      dlq: invoicePaidDlq      } = makeQueue(this, 'InvoicePaid',      'bl-invoice-paid');
    const { queue: paymentReceivedQ,  dlq: paymentReceivedDlq  } = makeQueue(this, 'PaymentReceived',  'bl-payment-received');
    const { queue: paymentConfirmedQ, dlq: paymentConfirmedDlq } = makeQueue(this, 'PaymentConfirmed', 'bl-payment-confirmed');

    // SNS → SQS subscriptions (rawMessageDelivery: MassTransit expects raw JSON)
    invoiceIssuedTopic.addSubscription   (new subscriptions.SqsSubscription(invoiceIssuedQ,    { rawMessageDelivery: true }));
    invoicePaidTopic.addSubscription     (new subscriptions.SqsSubscription(invoicePaidQ,      { rawMessageDelivery: true }));
    paymentReceivedTopic.addSubscription (new subscriptions.SqsSubscription(paymentReceivedQ,  { rawMessageDelivery: true }));
    paymentConfirmedTopic.addSubscription(new subscriptions.SqsSubscription(paymentConfirmedQ, { rawMessageDelivery: true }));

    // ─────────────────────────────────────────────────────────────────────────
    // IAM Roles
    // ─────────────────────────────────────────────────────────────────────────

    // Execution role: ECS agent uses this to pull images and read secrets at startup
    const executionRole = new iam.Role(this, 'EcsExecutionRole', {
      assumedBy:      new iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
      managedPolicies: [iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AmazonECSTaskExecutionRolePolicy')],
    });
    dbSecret.grantRead(executionRole);
    jwtSecret.grantRead(executionRole);
    webhookSecret.grantRead(executionRole);
    billingApiRepo.grantPull(executionRole);
    paymentsWorkerRepo.grantPull(executionRole);
    ledgerWorkerRepo.grantPull(executionRole);

    // Task role: running container uses this to access SNS/SQS at runtime
    const taskRole = new iam.Role(this, 'EcsTaskRole', {
      assumedBy: new iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
    });
    // Publish permissions
    invoiceIssuedTopic.grantPublish(taskRole);
    invoicePaidTopic.grantPublish(taskRole);
    paymentReceivedTopic.grantPublish(taskRole);
    paymentConfirmedTopic.grantPublish(taskRole);
    // Consume permissions (send/receive/delete)
    invoiceIssuedQ.grantConsumeMessages(taskRole);
    invoicePaidQ.grantConsumeMessages(taskRole);
    paymentReceivedQ.grantConsumeMessages(taskRole);
    paymentConfirmedQ.grantConsumeMessages(taskRole);

    // ─────────────────────────────────────────────────────────────────────────
    // CloudWatch Log Groups
    // ─────────────────────────────────────────────────────────────────────────
    const makeLg = (name: string) => new logs.LogGroup(this, `${name}Logs`, {
      logGroupName:    `/ecs/billing-ledger/${name}`,
      retention:       logs.RetentionDays.ONE_MONTH,
      removalPolicy:   RemovalPolicy.DESTROY,
    });
    const billingApiLg     = makeLg('billing-api');
    const paymentsWorkerLg = makeLg('payments-worker');
    const ledgerWorkerLg   = makeLg('ledger-worker');

    // ─────────────────────────────────────────────────────────────────────────
    // Shared environment + secret builders
    // ─────────────────────────────────────────────────────────────────────────
    const dbHost = db.instanceEndpoint.hostname;

    const sharedEnv = {
      'ConnectionStrings__Postgres': `Host=${dbHost};Port=5432;Database=billing_ledger`,
      'ConnectionStrings__Redis':    `${redis.attrRedisEndpointAddress}:${redis.attrRedisEndpointPort}`,
      'Messaging__Transport':        'SQS',
      'Messaging__Region':           this.region,
    };

    // Database credentials injected from Secrets Manager — never in plaintext env vars
    const dbSecrets = {
      'Database__User':     ecs.Secret.fromSecretsManager(dbSecret, 'username'),
      'Database__Password': ecs.Secret.fromSecretsManager(dbSecret, 'password'),
    };

    // ─────────────────────────────────────────────────────────────────────────
    // ECS Cluster
    // ─────────────────────────────────────────────────────────────────────────
    const cluster = new ecs.Cluster(this, 'Cluster', {
      vpc,
      clusterName:       'billing-ledger',
      containerInsights: true,          // enables detailed CloudWatch metrics per task
    });

    // ─────────────────────────────────────────────────────────────────────────
    // Billing.Api — internet-facing via ALB
    // ─────────────────────────────────────────────────────────────────────────
    const billingApiTaskDef = new ecs.FargateTaskDefinition(this, 'BillingApiTaskDef', {
      memoryLimitMiB: 512,
      cpu:            256,
      executionRole,
      taskRole,
    });

    const billingApiContainer = billingApiTaskDef.addContainer('billing-api', {
      image:   ecs.ContainerImage.fromEcrRepository(billingApiRepo, imageTag),
      logging: ecs.LogDrivers.awsLogs({ logGroup: billingApiLg, streamPrefix: 'api' }),
      environment: {
        ...sharedEnv,
        ASPNETCORE_ENVIRONMENT:             'Production',
        ASPNETCORE_URLS:                    'http://+:8080',
        'Messaging__BillingApiQueueName':   'bl-payment-confirmed',
        'SNS__InvoiceIssuedTopicArn':       invoiceIssuedTopic.topicArn,
        'SNS__InvoicePaidTopicArn':         invoicePaidTopic.topicArn,
        'SNS__PaymentReceivedTopicArn':     paymentReceivedTopic.topicArn,
      },
      // ► Secrets Manager values injected at container startup — ZERO plaintext exposure
      secrets: {
        ...dbSecrets,
        'Jwt__SigningKey':          ecs.Secret.fromSecretsManager(jwtSecret),
        'Payments__WebhookSecret': ecs.Secret.fromSecretsManager(webhookSecret),
      },
    });
    billingApiContainer.addPortMappings({ containerPort: 8080 });

    // ─────────────────────────────────────────────────────────────────────────
    // Payments.Worker — internal Fargate, no ALB (SQS pull model)
    // ─────────────────────────────────────────────────────────────────────────
    const paymentsWorkerTaskDef = new ecs.FargateTaskDefinition(this, 'PaymentsWorkerTaskDef', {
      memoryLimitMiB: 256,
      cpu:            256,
      executionRole,
      taskRole,
    });

    paymentsWorkerTaskDef.addContainer('payments-worker', {
      image:   ecs.ContainerImage.fromEcrRepository(paymentsWorkerRepo, imageTag),
      logging: ecs.LogDrivers.awsLogs({ logGroup: paymentsWorkerLg, streamPrefix: 'worker' }),
      environment: {
        ...sharedEnv,
        DOTNET_ENVIRONMENT:               'Production',
        'Messaging__QueueName':           'bl-payment-received',
        'SNS__PaymentConfirmedTopicArn':  paymentConfirmedTopic.topicArn,
      },
      secrets: { ...dbSecrets },
    });

    // ─────────────────────────────────────────────────────────────────────────
    // Ledger.Worker — internal Fargate, no ALB (SQS pull model)
    // ─────────────────────────────────────────────────────────────────────────
    const ledgerWorkerTaskDef = new ecs.FargateTaskDefinition(this, 'LedgerWorkerTaskDef', {
      memoryLimitMiB: 256,
      cpu:            256,
      executionRole,
      taskRole,
    });

    ledgerWorkerTaskDef.addContainer('ledger-worker', {
      image:   ecs.ContainerImage.fromEcrRepository(ledgerWorkerRepo, imageTag),
      logging: ecs.LogDrivers.awsLogs({ logGroup: ledgerWorkerLg, streamPrefix: 'worker' }),
      environment: {
        ...sharedEnv,
        DOTNET_ENVIRONMENT:                    'Production',
        'Messaging__InvoiceIssuedQueueName':   'bl-invoice-issued',
        'Messaging__InvoicePaidQueueName':     'bl-invoice-paid',
      },
      secrets: { ...dbSecrets },
    });

    // ─────────────────────────────────────────────────────────────────────────
    // Application Load Balancer — internet-facing, in front of Billing.Api ONLY
    // Workers run in private subnets with no inbound internet exposure.
    // ─────────────────────────────────────────────────────────────────────────
    const alb = new elbv2.ApplicationLoadBalancer(this, 'Alb', {
      vpc,
      internetFacing: true,
      securityGroup:  albSg,
      vpcSubnets:     { subnetType: ec2.SubnetType.PUBLIC },
    });

    const listener = alb.addListener('HttpListener', { port: 80, open: true });

    const billingApiService = new ecs.FargateService(this, 'BillingApiService', {
      cluster,
      taskDefinition: billingApiTaskDef,
      desiredCount:   1,
      securityGroups: [appSg],
      vpcSubnets:     { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      assignPublicIp: false,
    });

    listener.addTargets('BillingApiTarget', {
      port:     8080,
      protocol: elbv2.ApplicationProtocol.HTTP,
      targets:  [billingApiService],
      healthCheck: {
        path:             '/health',
        interval:         Duration.seconds(30),
        healthyHttpCodes: '200',
      },
    });

    // Workers: private subnet, no ALB, no public IP — outbound-only via NAT GW
    new ecs.FargateService(this, 'PaymentsWorkerService', {
      cluster,
      taskDefinition: paymentsWorkerTaskDef,
      desiredCount:   1,
      securityGroups: [appSg],
      vpcSubnets:     { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      assignPublicIp: false,
    });

    new ecs.FargateService(this, 'LedgerWorkerService', {
      cluster,
      taskDefinition: ledgerWorkerTaskDef,
      desiredCount:   1,
      securityGroups: [appSg],
      vpcSubnets:     { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      assignPublicIp: false,
    });

    // ─────────────────────────────────────────────────────────────────────────
    // CloudWatch Alarms
    // ─────────────────────────────────────────────────────────────────────────

    // DLQ depth > 0 means a message failed 3 retries — needs human investigation
    dlqAlarm(this, 'InvoiceIssuedDlqAlarm',    invoiceIssuedDlq,    'InvoiceIssued');
    dlqAlarm(this, 'InvoicePaidDlqAlarm',      invoicePaidDlq,      'InvoicePaid');
    dlqAlarm(this, 'PaymentReceivedDlqAlarm',  paymentReceivedDlq,  'PaymentReceived');
    dlqAlarm(this, 'PaymentConfirmedDlqAlarm', paymentConfirmedDlq, 'PaymentConfirmed');

    // ALB 5xx spike — likely Billing.Api crash or unhealthy container
    new cloudwatch.Alarm(this, 'Alb5xxAlarm', {
      metric: alb.metricHttpCodeElb(elbv2.HttpCodeElb.ELB_5XX_COUNT, {
        period:    Duration.minutes(5),
        statistic: 'Sum',
      }),
      threshold:          5,
      evaluationPeriods:  2,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
      alarmDescription:   'ALB returning 5xx — check /ecs/billing-ledger/billing-api logs',
      treatMissingData:   cloudwatch.TreatMissingData.NOT_BREACHING,
    });

    // ─────────────────────────────────────────────────────────────────────────
    // Stack Outputs
    // ─────────────────────────────────────────────────────────────────────────
    new cdk.CfnOutput(this, 'AlbDnsName',         { value: alb.loadBalancerDnsName,          description: 'Billing.Api entry point (configure CNAME to your domain)' });
    new cdk.CfnOutput(this, 'EcrBillingApi',       { value: billingApiRepo.repositoryUri,     description: 'ECR URI — push billing-api Docker image here' });
    new cdk.CfnOutput(this, 'EcrPaymentsWorker',   { value: paymentsWorkerRepo.repositoryUri, description: 'ECR URI — push payments-worker Docker image here' });
    new cdk.CfnOutput(this, 'EcrLedgerWorker',     { value: ledgerWorkerRepo.repositoryUri,   description: 'ECR URI — push ledger-worker Docker image here' });
    new cdk.CfnOutput(this, 'RdsEndpoint',         { value: db.instanceEndpoint.hostname,     description: 'RDS Postgres hostname (private — accessible from VPC only)' });
    new cdk.CfnOutput(this, 'DbSecretArn',         { value: dbSecret.secretArn,               description: 'Secrets Manager ARN for DB credentials' });
  }
}
