namespace FBus.Transports
open FBus
open RabbitMQ.Client
open RabbitMQ.Client.Events

//
// Binding model is as follow:
//
// ----------------------------------------------------------------------------------------------------------
//                   Exchanges                     |                         Queues
// ----------------------------------------------------------------------------------------------------------
//
//              Exchange Binding               Queue Binding
//
// fbus:msg:MsgType <--+--- fbus:shard:Client1 <------ fbus:client:Client1 (* concurrent and/or ephemeral *)
//                     |
//                     +--- fbus:shard:Client2 <--+--- fbus:client:Client2-1 (* sharded and/or ephemeral *)
//                                                |
//                                                +--- fbus:client:Client2-2
//


type RabbitMQ(uri, busConfig: BusConfiguration, msgCallback) =
    let channelLock = obj()
    let factory = ConnectionFactory(Uri = uri, AutomaticRecoveryEnabled = true)
    let conn = factory.CreateConnection()
    let channel = conn.CreateModel()
    let mutable sendChannel = conn.CreateModel()

    let tryGetHeaderAsString (key: string) (props: IBasicProperties) =
            match props.Headers.TryGetValue key with
            | true, (:? (byte[]) as s) -> Some (System.Text.Encoding.UTF8.GetString(s))
            | _ -> None


    // ========================================================================================================
    // WARNING: IModel is not thread safe: https://www.rabbitmq.com/dotnet-api-guide.html#concurrency
    // ========================================================================================================
    let safeSend headers xchgName routingKey body =
        let headers = headers |> Map.map (fun _ v -> v :> obj)

        let send () =
            if sendChannel.IsClosed then
                sendChannel <- conn.CreateModel()

            let props = sendChannel.CreateBasicProperties(Headers = headers, Persistent = true)
            sendChannel.BasicPublish(exchange = xchgName,
                                     routingKey = routingKey,
                                     basicProperties = props,
                                     body = body)

        lock channelLock send

    let safeAck (ea: BasicDeliverEventArgs) =
        let ack () =
            channel.BasicAck(deliveryTag = ea.DeliveryTag, multiple = false)

        lock channelLock ack

    let safeNack (ea: BasicDeliverEventArgs) =
        let nack() =
            channel.BasicNack(deliveryTag = ea.DeliveryTag, multiple = false, requeue = false)

        lock channelLock nack
    // ========================================================================================================


    let getExchangeMsg (msgType: string) = $"fbus:msg:{msgType}"

    let getExchangeShard (clientName: string) = $"fbus:shard:{clientName}"

    let getQueueClient (clientName: string) (shardName: string option) = 
        match shardName with
        | Some shardName -> $"fbus:client:{clientName}#{shardName}"
        | _ -> $"fbus:client:{clientName}"

    let configureAck () =
        channel.BasicQos(prefetchSize = 0ul, prefetchCount = 10us, ``global`` = false)
        channel.ConfirmSelect()

    let queueName = getQueueClient busConfig.Name busConfig.ShardName

    let configureDeadLettersQueues() =
       // ===============================================================================================
        // dead letter queues are bound to a single exchange (direct) - the routingKey is the target queue
        // ===============================================================================================
        let xchgDeadLetter = "fbus:dead-letter"
        let deadLetterQueueName = queueName + ":dead-letter"
        if busConfig.IsEphemeral then Map.empty
        else
            channel.ExchangeDeclare(exchange = xchgDeadLetter,
                                    ``type`` = ExchangeType.Direct,
                                    durable = true, autoDelete = false)
            channel.QueueDeclare(queue = deadLetterQueueName,
                                 durable = true, exclusive = false, autoDelete = false) |> ignore
            channel.QueueBind(queue = deadLetterQueueName, exchange = xchgDeadLetter, routingKey = deadLetterQueueName)

            Map [ "x-dead-letter-exchange", xchgDeadLetter :> obj
                  "x-dead-letter-routing-key", deadLetterQueueName :> obj ]

    let configureQueues queueArgs = 
        // =========================================================================================
        // message queues are bound to an exchange (fanout) - all bound subscribers receive messages
        // =========================================================================================
        channel.QueueDeclare(queueName,
                             durable = true, exclusive = false, autoDelete = busConfig.IsEphemeral,
                             arguments = queueArgs) |> ignore

    let bindExchangeAndQueue msgType =
        let xchgMsg = getExchangeMsg msgType
        channel.ExchangeDeclare(exchange = xchgMsg, ``type`` = ExchangeType.Fanout, 
                                durable = true, autoDelete = false)

        // NOTE: if exchange creation crashes then check "rabbitmq_consistent_hash_exchange" is correctly installed
        let xchgSub, xchgRouting = 
            match busConfig.ShardName with
            | Some _ -> let xchgShard = getExchangeShard busConfig.Name
                        channel.ExchangeDeclare(exchange = xchgShard, ``type`` = "x-consistent-hash", 
                                                durable = true, autoDelete = false)

                        channel.ExchangeBind(xchgShard, xchgMsg, routingKey = "")
                        xchgShard, "1"
            | _ -> xchgMsg, ""

        channel.QueueBind(queue = queueName, exchange = xchgSub, routingKey = xchgRouting)

    let subscribeMessages () =
        busConfig.Handlers |> Map.iter (fun msgType _ -> msgType |> bindExchangeAndQueue)

    let listenMessages () =
        let consumer = EventingBasicConsumer(channel)
        let consumerCallback msgCallback (ea: BasicDeliverEventArgs) =
            try
                let headers = ea.BasicProperties.Headers |> Seq.choose (fun kvp -> ea.BasicProperties
                                                                                   |> tryGetHeaderAsString kvp.Key
                                                                                   |> Option.map (fun s -> kvp.Key, s))
                                                         |> Map

                msgCallback headers ea.Body

                safeAck ea
            with
                | _ -> safeNack ea

        consumer.Received.Add (consumerCallback msgCallback)
        channel.BasicConsume(queue = queueName, autoAck = false, consumer = consumer) |> ignore

    do
        configureAck()
        configureDeadLettersQueues() |> configureQueues
        subscribeMessages ()
        listenMessages()

    interface IBusTransport with
        member _.Publish headers msgType body key =
            let xchgName = getExchangeMsg msgType
            safeSend headers xchgName key body

        member _.Send headers client msgType body key =
            let xchgName = getExchangeShard client
            safeSend headers xchgName key body

        member _.Dispose() =
            sendChannel.Dispose()
            channel.Dispose()
            conn.Dispose()
