namespace FBus.Transports
open FBus
open FBus.Builder
open RabbitMQ.Client
open RabbitMQ.Client.Events

type RabbitMQ(busConfig: BusConfiguration, msgCallback) =
    let channelLock = obj()
    let factory = ConnectionFactory(Uri = busConfig.Uri, AutomaticRecoveryEnabled = true)
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
    let safeSend headers xchgName routingKey msgType body =
        let headers = headers |> Map.map (fun _ v -> v :> obj) |> Map.add "fbus:msgtype" (msgType :> obj)

        let send () =
            if sendChannel.IsClosed then
                sendChannel.Dispose()
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


    let getClientQueue name = sprintf "fbus:client:%s" name

    let getExchangeName (msgType: string) = msgType |> sprintf "fbus:type:%s"

    let configureAck () =
        channel.BasicQos(prefetchSize = 0ul, prefetchCount = 1us, ``global`` = false)
        channel.ConfirmSelect()

    let queueName = getClientQueue busConfig.Name

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
        let xchgName = getExchangeName msgType
        channel.ExchangeDeclare(exchange = xchgName, ``type`` = ExchangeType.Fanout, 
                                durable = true, autoDelete = false)
        channel.QueueBind(queue = queueName, exchange = xchgName, routingKey = "")

    let subscribeMessages () =
        busConfig.Handlers |> Map.iter (fun msgType _ -> msgType |> bindExchangeAndQueue)

    let listenMessages () =
        let consumer = EventingBasicConsumer(channel)
        let consumerCallback msgCallback (ea: BasicDeliverEventArgs) =
            try
                let msgType = match ea.BasicProperties |> tryGetHeaderAsString "fbus:msgtype" with
                              | Some msgType -> msgType
                              | _ -> failwithf "Missing header fbus:msgtype"

                let headers = ea.BasicProperties.Headers |> Seq.choose (fun kvp -> ea.BasicProperties |> tryGetHeaderAsString kvp.Key |> Option.map (fun s -> kvp.Key, s))
                                                         |> Map

                msgCallback headers msgType ea.Body

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
        member _.Publish headers msgType body =
            let xchgName = getExchangeName msgType
            safeSend headers xchgName "" msgType body

        member _.Send headers client msgType body =
            let routingKey = getClientQueue client
            safeSend headers "" routingKey msgType body

        member _.Dispose() =
            sendChannel.Dispose()
            channel.Dispose()
            conn.Dispose()
