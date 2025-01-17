namespace FBus.GenericHost
open System
open Microsoft.Extensions.DependencyInjection
open FBus
open FBus.Containers
open System.Runtime.CompilerServices

[<Extension>]
type Extensions =
    [<Extension>]
    static member AddFBus(services: IServiceCollection, configurator: BusBuilder -> BusBuilder) =
        let busControl = Builder.configure() |> configurator
                                             |> Builder.withContainer (GenericHost(services))
                                             |> Builder.build

        let busInitiator = busControl :?> IBusInitiator

        services.AddSingleton(busControl)
                .AddSingleton(busInitiator)
                .AddHostedService<BusService>() |> ignore
