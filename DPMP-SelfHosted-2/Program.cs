using System.Net;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Xml.Linq;
using System.Collections.Concurrent;
using System.Security.Cryptography;

List<Vehicle> vehicles = new();

var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:8080/");
listener.Start();

var httpClient = new HttpClient();

httpClient.DefaultRequestHeaders.Add(
    "User-Agent",
    "Mozilla/5.0 (X11; Linux x86_64; rv:155.0) Gecko/20100101 Firefox/155.0"
);

httpClient.DefaultRequestHeaders.Add("Origin", "https://online.dpmp.cz");
httpClient.DefaultRequestHeaders.Add("Referer", "https://online.dpmp.cz/");

var valhallaLock = new SemaphoreSlim(1, 1);

while (true)
{
    var context = await listener.GetContextAsync();
    var path = context.Request.Url!.AbsolutePath;

    try
    {
        if (path == "/api/autobusy")
        {
            var data = await GetDpmp(
                httpClient,
                "https://api.mhdonline.cz/pardubice/vehicles"
            );

            vehicles = ParseVehicles(data);

            await SendJson(context, vehicles);
            continue;
        }

        if (path == "/api/closest-bus")
        {
            var latitude = double.Parse(context.Request.QueryString["lat"]!);
            var longitude = double.Parse(context.Request.QueryString["lon"]!);

            var data = await GetDpmp(
                httpClient,
                "https://api.mhdonline.cz/pardubice/vehicles"
            );

            vehicles = ParseVehicles(data);

            Vehicle? closest = null;
            double closestDistance = double.MaxValue;

            foreach (var bus in vehicles)
            {
                if (!double.TryParse(bus.GpsLat, out var busLat) ||
                    !double.TryParse(bus.GpsLon, out var busLon))
                    continue;

                var distance = GetDistance(
                    latitude,
                    longitude,
                    busLat,
                    busLon
                );

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = bus;
                }
            }

            var result = closest == null
                ? new Closestbus()
                : new Closestbus
                {
                    distance = closestDistance * 1000,
                    vid = closest.vid,
                    lat = closest.GpsLat,
                    lon = closest.GpsLon
                };

            await SendJson(context, result);
            continue;
        }

        if (path == "/api/stop-arrivals")
        {
            var stopId = int.Parse(context.Request.QueryString["stopId"]!);

            var data = await GetDpmp(
                httpClient,
                $"https://api.mhdonline.cz/pardubice/stops/{stopId}/incomingArrivals"
            );

            await SendResponse(
                context,
                data,
                data.TrimStart().StartsWith("<")
                    ? "application/xml"
                    : "application/json"
            );

            continue;
        }

        if (path == "/api/stops")
        {
            var data = await GetDpmp(
                httpClient,
                "https://api.mhdonline.cz/pardubice/stops"
            );

            var stops = ParseStops(data);

            await SendJson(context, stops);
            continue;
        }

        if (path == "/api/route")
        {
            await HandleRoute(
                context,
                httpClient,
                valhallaLock
            );

            continue;
        }

        context.Response.StatusCode = 404;
        context.Response.Close();
    }
    catch (Exception ex)
    {
        await SendJson(
            context,
            new { error = ex.Message },
            500
        );
    }
}

static async Task HandleRoute(
    HttpListenerContext context,
    HttpClient httpClient,
    SemaphoreSlim valhallaLock
)
{
    var latitude = double.Parse(
        context.Request.QueryString["lat"]!
    );

    var longitude = double.Parse(
        context.Request.QueryString["lon"]!
    );

    var toStop = int.Parse(
        context.Request.QueryString["toStop"]!
    );

    var stopsData = await GetDpmp(
        httpClient,
        "https://api.mhdonline.cz/pardubice/stops"
    );

    var stops = ParseStops(stopsData);

    var destinationStop =
        stops.FirstOrDefault(s => s.id == toStop);

    if (destinationStop == null)
    {
        await SendJson(
            context,
            new { error = "Destination stop not found." },
            404
        );

        return;
    }

    var nearestStops = stops
        .OrderBy(stop =>
            GetDistance(
                latitude,
                longitude,
                stop.gpsLat,
                stop.gpsLon
            )
        )
        .Take(8)
        .ToList();

    const int safetyMarginMinutes = 1;
    const int maxLegs = 4;
    const int maxRoutes = 6;

    var walkingTimes =
        new Dictionary<int, double>();

    foreach (var stop in nearestStops)
    {
        walkingTimes[stop.id] =
            await GetwalkingTime(
                httpClient,
                latitude,
                longitude,
                stop.gpsLat,
                stop.gpsLon,
                valhallaLock
            );
    }

    var arrivalsCache =
        new ConcurrentDictionary<int, Task<JsonDocument>>();

    async Task<JsonDocument> GetArrivals(int stopId)
    {
        return await arrivalsCache.GetOrAdd(
            stopId,
            id => GetStopArrivals(httpClient, id)
        );
    }

    var routeCandidates =
        new List<RouteResult>();

    var serviceDates = new[]
    {
        DateTime.Today,
        DateTime.Today.AddDays(1)
    };

    foreach (var serviceDate in serviceDates)
    {
        var queue =
            new PriorityQueue<RouteState, DateTime>();

        var bestArrivalAtState =
            new Dictionary<string, DateTime>();

        foreach (var stop in nearestStops)
        {
            var walkingTime =
                walkingTimes[stop.id];

            DateTime arrivalAtStop;

            if (serviceDate.Date == DateTime.Today)
            {
                arrivalAtStop =
                    DateTime.Now +
                    TimeSpan.FromSeconds(walkingTime);
            }
            else
            {
                arrivalAtStop =
                    serviceDate.Date +
                    TimeSpan.FromSeconds(walkingTime);
            }

            queue.Enqueue(
                new RouteState
                {
                    stopId = stop.id,
                    arrivalTime = arrivalAtStop,
                    firstStop = stop.id,
                    firstStopName = stop.name,
                    walkingTime = walkingTime,
                    legs = new List<RouteLeg>()
                },
                arrivalAtStop
            );

            bestArrivalAtState[
                $"{stop.id}|"
            ] = arrivalAtStop;
        }

        while (queue.Count > 0)
        {
            var state =
                queue.Dequeue();

            if (state.stopId == toStop &&
                state.legs.Count > 0)
            {
                var result =
                    BuildRouteResult(
                        state,
                        destinationStop,
                        stops
                    );

                if (!routeCandidates.Any(r =>
                    r.lineId == result.lineId &&
                    r.connectionId == result.connectionId &&
                    r.fromStop == result.fromStop &&
                    r.toStop == result.toStop))
                {
                    routeCandidates.Add(result);
                }

                if (routeCandidates.Count >= maxRoutes)
                    break;

                continue;
            }

            if (state.legs.Count >= maxLegs)
                continue;

            var lastLeg =
                state.legs.LastOrDefault();

            var stateKey =
                lastLeg == null
                    ? $"{state.stopId}|"
                    : $"{state.stopId}|{lastLeg.lineId}|{lastLeg.connectionId}";

            if (
                bestArrivalAtState.TryGetValue(
                    stateKey,
                    out var knownArrival
                ) &&
                state.arrivalTime >
                knownArrival.AddSeconds(1)
            )
            {
                continue;
            }

            var arrivals =
                await GetArrivals(state.stopId);

            if (
                arrivals.RootElement.ValueKind !=
                JsonValueKind.Array
            )
            {
                continue;
            }

            foreach (
                var connection
                in arrivals.RootElement.EnumerateArray()
            )
            {
                if (!connection.TryGetProperty(
                        "stops",
                        out var connectionStops))
                {
                    continue;
                }

                var times =
                    GetConnectionTimes(
                        connection,
                        serviceDate
                    );

                var lineId =
                    connection
                        .GetProperty("lineId")
                        .ToString();

                var connectionId =
                    connection
                        .GetProperty("connectionId")
                        .ToString();

                /*
                 * Find the FIRST usable occurrence of the current stop.
                 *
                 * This is important for looping connections where the same
                 * stop can appear more than once.
                 */
                int boardingIndex = -1;

                for (
                    int i = 0;
                    i < connectionStops.GetArrayLength();
                    i++
                )
                {
                    var stop =
                        connectionStops[i];

                    var stopId =
                        stop
                            .GetProperty("stopId")
                            .GetInt32();

                    if (stopId != state.stopId)
                        continue;

                    var departure =
                        times[i].departure;

                    if (!departure.HasValue)
                        continue;

                    /*
                     * THIS is the important catchability check.
                     *
                     * We compare:
                     *
                     *     arrival at boarding stop
                     *                 +
                     *          safety margin
                     *
                     * against:
                     *
                     *     departure FROM boarding stop
                     *
                     * The destination arrival is NOT involved here.
                     */
                    if (
                        departure.Value >=
                        state.arrivalTime +
                        TimeSpan.FromMinutes(
                            safetyMarginMinutes
                        )
                    )
                    {
                        boardingIndex = i;
                        break;
                    }
                }

                if (boardingIndex == -1)
                    continue;

                var boardingDeparture =
                    times[boardingIndex].departure!.Value;

                /*
                 * Once we have caught this connection,
                 * examine every later stop it visits.
                 */
                for (
                    int j = boardingIndex + 1;
                    j < connectionStops.GetArrayLength();
                    j++
                )
                {
                    var nextStop =
                        connectionStops[j];

                    var nextStopId =
                        nextStop
                            .GetProperty("stopId")
                            .GetInt32();

                    var arrival =
                        times[j].arrival ??
                        times[j].departure;

                    if (!arrival.HasValue)
                        continue;

                    /*
                     * Never allow a stop whose timetable time is before
                     * the boarding departure.
                     */
                    if (
                        arrival.Value <
                        boardingDeparture
                    )
                    {
                        continue;
                    }

                    var leg =
                        CreateLeg(
                            connection,
                            connectionStops[boardingIndex],
                            nextStop,
                            state.stopId,
                            nextStopId,
                            boardingDeparture,
                            arrival.Value,
                            stops
                        );

                    /*
                     * DESTINATION ARRIVAL IS ONLY USED HERE:
                     *
                     * We already know the bus can be caught.
                     * Now we can use its arrival time to construct
                     * the completed route.
                     */
                    if (nextStopId == toStop)
                    {
                        var destinationLegs =
                            new List<RouteLeg>(
                                state.legs
                            );

                        destinationLegs.Add(leg);

                        var resultState =
                            new RouteState
                            {
                                stopId = toStop,
                                arrivalTime =
                                    arrival.Value,
                                firstStop =
                                    state.firstStop,
                                firstStopName =
                                    state.firstStopName,
                                walkingTime =
                                    state.walkingTime,
                                legs =
                                    destinationLegs
                            };

                        var result =
                            BuildRouteResult(
                                resultState,
                                destinationStop,
                                stops
                            );

                        if (!routeCandidates.Any(r =>
                            r.lineId == result.lineId &&
                            r.connectionId == result.connectionId &&
                            r.fromStop == result.fromStop &&
                            r.toStop == result.toStop))
                        {
                            routeCandidates.Add(result);
                        }

                        if (routeCandidates.Count >= maxRoutes)
                            break;
                    }

                    if (routeCandidates.Count >= maxRoutes)
                        break;

                    /*
                     * Do not transfer onto the exact same connection.
                     */
                    if (
                        lastLeg != null &&
                        lastLeg.lineId == lineId &&
                        lastLeg.connectionId == connectionId
                    )
                    {
                        continue;
                    }

                    var transferLegs =
                        new List<RouteLeg>(
                            state.legs
                        );

                    transferLegs.Add(leg);

                    var nextStateKey =
                        $"{nextStopId}|{lineId}|{connectionId}";

                    if (
                        !bestArrivalAtState.TryGetValue(
                            nextStateKey,
                            out var previousArrival
                        ) ||
                        arrival.Value < previousArrival
                    )
                    {
                        bestArrivalAtState[
                            nextStateKey
                        ] = arrival.Value;

                        queue.Enqueue(
                            new RouteState
                            {
                                stopId = nextStopId,

                                /*
                                 * IMPORTANT:
                                 * The state time is the arrival time
                                 * at THIS transfer stop.
                                 *
                                 * The next bus will therefore be
                                 * compared against this time.
                                 */
                                arrivalTime =
                                    arrival.Value,

                                firstStop =
                                    state.firstStop,

                                firstStopName =
                                    state.firstStopName,

                                walkingTime =
                                    state.walkingTime,

                                legs =
                                    transferLegs
                            },
                            arrival.Value
                        );
                    }
                }

                if (routeCandidates.Count >= maxRoutes)
                    break;
            }

            if (routeCandidates.Count >= maxRoutes)
                break;
        }

        if (routeCandidates.Count >= maxRoutes)
            break;
    }

    /*
     * Only now do we sort the completed routes by their
     * final destination arrival time.
     *
     * This does NOT affect whether a bus was catchable.
     */
    routeCandidates =
        routeCandidates
            .OrderBy(r =>
                ParseRouteDateTime(
                    r.arrivalTime
                )
            )
            .Take(maxRoutes)
            .ToList();

    foreach (var route in routeCandidates)
    {
        var fromStop =
            stops.FirstOrDefault(
                s => s.id == route.fromStop
            );

        if (fromStop == null)
            continue;

        route.walkingTime =
            walkingTimes.TryGetValue(
                fromStop.id,
                out var walkingTime
            )
                ? walkingTime
                : await GetwalkingTime(
                    httpClient,
                    latitude,
                    longitude,
                    fromStop.gpsLat,
                    fromStop.gpsLon,
                    valhallaLock
                );
    }

    await SendJson(
        context,
        routeCandidates
    );
}

static RouteResult BuildRouteResult(
    RouteState state,
    Stop destinationStop,
    List<Stop> stops
)
{
    var firstLeg = state.legs.First();
    var lastLeg = state.legs.Last();

    return new RouteResult
    {
        lineId =
            string.Join(
                " → ",
                state.legs.Select(x => x.lineId)
            ),

        connectionId =
            string.Join(
                " → ",
                state.legs.Select(x => x.connectionId)
            ),

        fromStop = state.firstStop,
        fromStopName = state.firstStopName,
        fromPlatform = firstLeg.fromPlatform,

        toStop = destinationStop.id,
        toStopName = destinationStop.name,
        toPlatform = lastLeg.toPlatform,

        departureTime =
            firstLeg.departureTime,

        arrivalTime =
            lastLeg.arrivalTime,

        walkingTime =
            state.walkingTime
    };
}

static RouteLeg CreateLeg(
    JsonElement connection,
    JsonElement from,
    JsonElement to,
    int fromStop,
    int toStop,
    DateTime departure,
    DateTime arrival,
    List<Stop> stops
)
{
    var lineId =
        connection
            .GetProperty("lineId")
            .ToString();

    var connectionId =
        connection
            .GetProperty("connectionId")
            .ToString();

    return new RouteLeg
    {
        lineId = lineId,
        connectionId = connectionId,

        fromStop = fromStop,

        fromStopName =
            stops.First(
                s => s.id == fromStop
            ).name,

        fromPlatform =
            from.GetProperty(
                "platformId"
            ).ToString(),

        toStop = toStop,

        toStopName =
            stops.FirstOrDefault(
                s => s.id == toStop
            )?.name ?? "",

        toPlatform =
            to.GetProperty(
                "platformId"
            ).ToString(),

        departureTime =
            departure.ToString(
                "yyyy-MM-dd HH:mm:ss"
            ),

        arrivalTime =
            arrival.ToString(
                "yyyy-MM-dd HH:mm:ss"
            )
    };
}

static DateTime ParseRouteDateTime(
    string value
)
{
    return DateTime.TryParse(
        value,
        out var result
    )
        ? result
        : DateTime.MaxValue;
}

static async Task<string> GetDpmp(
    HttpClient httpClient,
    string url
)
{
    using var request =
        new HttpRequestMessage(
            HttpMethod.Get,
            url
        );

    request.Headers.TryAddWithoutValidation(
        "X-App-Protocol",
        await GetAppProtocol()
    );

    request.Headers.TryAddWithoutValidation(
        "Accept",
        "*/*"
    );

    var response =
        await httpClient.SendAsync(request);

    response.EnsureSuccessStatusCode();

    return await response.Content.ReadAsStringAsync();
}

static async Task<string> GetAppProtocol()
{
    const string seed =
        "your-public-protocol-seed";

    long window =
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        / (15 * 60 * 1000);

    using var hmac =
        new HMACSHA256(
            Encoding.UTF8.GetBytes(seed)
        );

    var hash =
        hmac.ComputeHash(
            Encoding.UTF8.GetBytes(
                window.ToString()
            )
        );

    return Convert.ToHexString(hash)
        .ToLowerInvariant();
}

static List<Vehicle> ParseVehicles(string data)
{
    data = CleanResponse(data);

    if (data.StartsWith("<"))
    {
        var vehicleXml =
            XDocument.Parse(data);

        return vehicleXml
            .Descendants()
            .Where(
                x =>
                    x.Name.LocalName.Equals(
                        "Vehicle",
                        StringComparison.OrdinalIgnoreCase
                    )
            )
            .Select(
                vehicle =>
                    new Vehicle
                    {
                        vid =
                            vehicle.Attribute("vid")?.Value ??
                            vehicle.Attribute("id")?.Value ??
                            "",

                        lineId =
                            vehicle.Attribute("lineId")?.Value ??
                            "",

                        lineDirection =
                            vehicle.Attribute("lineDirection")?.Value ??
                            "",

                        destinationName =
                            vehicle.Attribute("destinationName")?.Value ??
                            "",

                        lastStopId =
                            vehicle.Attribute("lastStopId")?.Value ??
                            "",

                        lastStopName =
                            vehicle.Attribute("lastStopName")?.Value ??
                            "",

                        nextStopId =
                            vehicle.Attribute("nextStopId")?.Value ??
                            "",

                        nextStopName =
                            vehicle.Attribute("nextStopName")?.Value ??
                            "",

                        afterNextStopId =
                            vehicle.Attribute("afterNextStopId")?.Value ??
                            "",

                        afterNextStopName =
                            vehicle.Attribute("afterNextStopName")?.Value ??
                            "",

                        nextStopScheduledDepartureTime =
                            vehicle.Attribute(
                                "nextStopScheduledDepartureTime"
                            )?.Value ?? "",

                        GpsLat =
                            vehicle.Attribute("gpsLat")?.Value ??
                            "",

                        GpsLon =
                            vehicle.Attribute("gpsLon")?.Value ??
                            "",

                        currentDelay =
                            vehicle.Attribute("currentDelay")?.Value ??
                            "",

                        connectionId =
                            vehicle.Attribute("connectionId")?.Value ??
                            "",

                        airConditioning =
                            vehicle.Attribute("airConditioning")?.Value ??
                            "",

                        onStation =
                            vehicle.Attribute("onStation")?.Value ??
                            "",

                        nextStopIsFinal =
                            vehicle.Attribute("nextStopIsFinal")?.Value ??
                            ""
                    }
            )
            .ToList();
    }

    using var vehicleJson =
        JsonDocument.Parse(data);

    var root =
        vehicleJson.RootElement;

    JsonElement array;

    if (root.ValueKind == JsonValueKind.Array)
    {
        array = root;
    }
    else if (
        root.TryGetProperty(
            "vehicles",
            out var vehiclesProperty
        )
    )
    {
        array = vehiclesProperty;
    }
    else
    {
        throw new Exception(
            "JSON does not contain a vehicles array."
        );
    }

    return array
        .EnumerateArray()
        .Select(
            vehicle =>
                new Vehicle
                {
                    vid = GetString(vehicle, "vid"),
                    lineId = GetString(vehicle, "lineId"),
                    lineDirection =
                        GetString(vehicle, "lineDirection"),
                    destinationName =
                        GetString(vehicle, "destinationName"),
                    lastStopId =
                        GetString(vehicle, "lastStopId"),
                    lastStopName =
                        GetString(vehicle, "lastStopName"),
                    nextStopId =
                        GetString(vehicle, "nextStopId"),
                    nextStopName =
                        GetString(vehicle, "nextStopName"),
                    afterNextStopId =
                        GetString(vehicle, "afterNextStopId"),
                    afterNextStopName =
                        GetString(vehicle, "afterNextStopName"),
                    nextStopScheduledDepartureTime =
                        GetString(
                            vehicle,
                            "nextStopScheduledDepartureTime"
                        ),
                    GpsLat =
                        GetString(vehicle, "gpsLat"),
                    GpsLon =
                        GetString(vehicle, "gpsLon"),
                    currentDelay =
                        GetString(vehicle, "currentDelay"),
                    connectionId =
                        GetString(vehicle, "connectionId"),
                    airConditioning =
                        GetString(vehicle, "airConditioning"),
                    onStation =
                        GetString(vehicle, "onStation"),
                    nextStopIsFinal =
                        GetString(vehicle, "nextStopIsFinal")
                }
        )
        .ToList();
}

static string GetString(
    JsonElement element,
    string name
)
{
    return element.TryGetProperty(
        name,
        out var value
    )
        ? value.ToString()
        : "";
}

static List<Stop> ParseStops(string data)
{
    data = CleanResponse(data);

    if (data.StartsWith("<"))
    {
        var stopXml =
            XDocument.Parse(data);

        return stopXml
            .Descendants("Stop")
            .Select(
                stop =>
                    new Stop
                    {
                        id =
                            int.Parse(
                                stop.Attribute("id")!.Value
                            ),

                        name =
                            stop.Attribute("name")?.Value ??
                            "",

                        gpsLat =
                            double.Parse(
                                stop.Attribute("gpsLat")!.Value
                            ),

                        gpsLon =
                            double.Parse(
                                stop.Attribute("gpsLon")!.Value
                            )
                    }
            )
            .ToList();
    }

    using var stopsJson =
        JsonDocument.Parse(data);

    var root =
        stopsJson.RootElement;

    JsonElement array;

    if (root.ValueKind == JsonValueKind.Array)
    {
        array = root;
    }
    else if (
        root.TryGetProperty(
            "stops",
            out var stopsProperty
        )
    )
    {
        array = stopsProperty;
    }
    else
    {
        throw new Exception(
            "JSON does not contain a stops array."
        );
    }

    return array
        .EnumerateArray()
        .Select(
            stop =>
                new Stop
                {
                    id =
                        stop.GetProperty("id").GetInt32(),

                    name =
                        stop.GetProperty("name").GetString() ?? "",

                    gpsLat =
                        stop.GetProperty("gpsLat").GetDouble(),

                    gpsLon =
                        stop.GetProperty("gpsLon").GetDouble()
                }
        )
        .ToList();
}

static async Task<JsonDocument> GetStopArrivals(
    HttpClient httpClient,
    int stopId
)
{
    var data =
        await GetDpmp(
            httpClient,
            $"https://api.mhdonline.cz/pardubice/stops/{stopId}/incomingArrivals"
        );

    data =
        CleanResponse(data);

    if (data.StartsWith("<"))
    {
        var arrivalsXml =
            XDocument.Parse(data);

        var connections =
            arrivalsXml
                .Descendants("Connection")
                .Select(
                    connection =>
                        new
                        {
                            lineId =
                                connection.Attribute(
                                    "lineId"
                                )?.Value ?? "",

                            connectionId =
                                connection.Attribute(
                                    "connectionId"
                                )?.Value ?? "",

                            fixedCodes =
                                connection
                                    .Elements("fixedCodes")
                                    .Select(x => x.Value)
                                    .ToArray(),

                            stops =
                                connection
                                    .Elements(
                                        "ConnectionStop"
                                    )
                                    .Select(
                                        stop =>
                                            new
                                            {
                                                stopId =
                                                    int.Parse(
                                                        stop.Attribute(
                                                            "stopId"
                                                        )!.Value
                                                    ),

                                                platformId =
                                                    stop.Attribute(
                                                        "platformId"
                                                    )?.Value ?? "",

                                                departureTime =
                                                    stop.Attribute(
                                                        "departureTime"
                                                    )?.Value,

                                                arrivalTime =
                                                    stop.Attribute(
                                                        "arrivalTime"
                                                    )?.Value
                                            }
                                    )
                                    .ToArray()
                        }
                )
                .ToArray();

        return JsonDocument.Parse(
            JsonSerializer.Serialize(
                connections
            )
        );
    }

    return JsonDocument.Parse(data);
}

static List<(DateTime? arrival, DateTime? departure)>
    GetConnectionTimes(
        JsonElement connection,
        DateTime serviceDate
    )
{
    var stops =
        connection.GetProperty("stops");

    var result =
        new List<(DateTime? arrival, DateTime? departure)>(
            stops.GetArrayLength()
        );

    DateTime? previous = null;

    for (
        int i = 0;
        i < stops.GetArrayLength();
        i++
    )
    {
        var stop = stops[i];

        TimeSpan? arrivalTime = null;
        TimeSpan? departureTime = null;

        if (
            stop.TryGetProperty(
                "arrivalTime",
                out var arrival
            ) &&
            arrival.ValueKind == JsonValueKind.String &&
            TimeSpan.TryParse(
                arrival.GetString(),
                out var parsedArrival
            )
        )
        {
            arrivalTime = parsedArrival;
        }

        if (
            stop.TryGetProperty(
                "departureTime",
                out var departure
            ) &&
            departure.ValueKind == JsonValueKind.String &&
            TimeSpan.TryParse(
                departure.GetString(),
                out var parsedDeparture
            )
        )
        {
            departureTime = parsedDeparture;
        }

        DateTime? arrivalDateTime =
            arrivalTime.HasValue
                ? serviceDate.Date + arrivalTime.Value
                : null;

        DateTime? departureDateTime =
            departureTime.HasValue
                ? serviceDate.Date + departureTime.Value
                : null;

        var reference =
            arrivalDateTime ??
            departureDateTime;

        if (
            reference.HasValue &&
            previous.HasValue &&
            reference.Value < previous.Value
        )
        {
            arrivalDateTime =
                arrivalDateTime?.AddDays(1);

            departureDateTime =
                departureDateTime?.AddDays(1);
        }

        if (arrivalDateTime.HasValue)
            previous = arrivalDateTime;

        if (
            departureDateTime.HasValue &&
            (!previous.HasValue ||
             departureDateTime.Value > previous.Value)
        )
        {
            previous = departureDateTime;
        }

        result.Add(
            (
                arrivalDateTime,
                departureDateTime
            )
        );
    }

    return result;
}

static double EstimateWalkingTime(
    double fromLat,
    double fromLon,
    double toLat,
    double toLon
)
{
    var distance =
        GetDistance(
            fromLat,
            fromLon,
            toLat,
            toLon
        );

    return distance * 1000 / 1.35;
}

static async Task<double> GetwalkingTime(
    HttpClient httpClient,
    double fromLat,
    double fromLon,
    double toLat,
    double toLon,
    SemaphoreSlim valhallaLock
)
{
    await valhallaLock.WaitAsync();

    try
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var request =
                new
                {
                    locations = new[]
                    {
                        new
                        {
                            lat = fromLat,
                            lon = fromLon
                        },

                        new
                        {
                            lat = toLat,
                            lon = toLon
                        }
                    },

                    costing = "pedestrian"
                };

            var response =
                await httpClient.PostAsync(
                    "https://valhalla1.openstreetmap.de/route",
                    new StringContent(
                        JsonSerializer.Serialize(request),
                        Encoding.UTF8,
                        "application/json"
                    )
                );

            if ((int)response.StatusCode == 429)
            {
                await Task.Delay(
                    1500 * (attempt + 1)
                );

                continue;
            }

            response.EnsureSuccessStatusCode();

            using var valhallaJson =
                JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync()
                );

            return valhallaJson.RootElement
                .GetProperty("trip")
                .GetProperty("summary")
                .GetProperty("time")
                .GetDouble();
        }

        throw new Exception(
            "Valhalla rate limit reached."
        );
    }
    finally
    {
        valhallaLock.Release();
    }
}

static double GetDistance(
    double lat1,
    double lon1,
    double lat2,
    double lon2
)
{
    const double earthRadiusKm = 6371;

    var dLat =
        degreetoRadians(lat2 - lat1);

    var dLon =
        degreetoRadians(lon2 - lon1);

    var a =
        Hav(dLat) +
        Math.Cos(degreetoRadians(lat1)) *
        Math.Cos(degreetoRadians(lat2)) *
        Hav(dLon);

    var c =
        2 *
        Math.Asin(
            Math.Sqrt(a)
        );

    return earthRadiusKm * c;
}

static double degreetoRadians(
    double degree
)
{
    return degree *
           Math.PI /
           180;
}

static double Hav(double theta)
{
    var s =
        Math.Sin(theta / 2);

    return s * s;
}

static string CleanResponse(string data)
{
    return data.TrimStart(
        '\uFEFF',
        '\u200B',
        '\u200C',
        '\u200D',
        ' ',
        '\t',
        '\r',
        '\n'
    );
}

static async Task SendJson(
    HttpListenerContext context,
    object data,
    int statusCode = 200
)
{
    context.Response.StatusCode = statusCode;

    context.Response.Headers["Access-Control-Allow-Origin"] =
        "http://localhost:8000";

    context.Response.ContentType =
        "application/json";

    var bytes =
        Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(data)
        );

    await context.Response.OutputStream.WriteAsync(bytes);
    context.Response.Close();
}

static async Task SendResponse(
    HttpListenerContext context,
    string data,
    string contentType
)
{
    context.Response.Headers["Access-Control-Allow-Origin"] =
        "http://localhost:8000";

    context.Response.ContentType =
        contentType;

    var bytes =
        Encoding.UTF8.GetBytes(data);

    await context.Response.OutputStream.WriteAsync(bytes);
    context.Response.Close();
}

class RouteState
{
    public int stopId { get; set; }
    public DateTime arrivalTime { get; set; }
    public int firstStop { get; set; }
    public string firstStopName { get; set; } = "";
    public double walkingTime { get; set; }
    public List<RouteLeg> legs { get; set; } = new();
}

class RouteLeg
{
    public string lineId { get; set; } = "";
    public string connectionId { get; set; } = "";
    public int fromStop { get; set; }
    public string fromStopName { get; set; } = "";
    public string fromPlatform { get; set; } = "";
    public int toStop { get; set; }
    public string toStopName { get; set; } = "";
    public string toPlatform { get; set; } = "";
    public string departureTime { get; set; } = "";
    public string arrivalTime { get; set; } = "";
}

class RouteResult
{
    public string lineId { get; set; } = "";
    public string connectionId { get; set; } = "";
    public int fromStop { get; set; }
    public int toStop { get; set; }
    public string fromPlatform { get; set; } = "";
    public string toPlatform { get; set; } = "";
    public string departureTime { get; set; } = "";
    public string arrivalTime { get; set; } = "";
    public double walkingTime { get; set; }
    public string fromStopName { get; set; } = "";
    public string toStopName { get; set; } = "";
}

class Stop
{
    public int id { get; set; }
    public string name { get; set; } = "";
    public double gpsLat { get; set; }
    public double gpsLon { get; set; }
}

class Closestbus
{
    public string vid { get; set; } = "";
    public double distance { get; set; }
    public string lat { get; set; } = "";
    public string lon { get; set; } = "";
}

class Vehicle
{
    public string vid { get; set; } = "";
    public string lineId { get; set; } = "";
    public string lineDirection { get; set; } = "";
    public string destinationName { get; set; } = "";
    public string lastStopId { get; set; } = "";
    public string lastStopName { get; set; } = "";
    public string nextStopId { get; set; } = "";
    public string nextStopName { get; set; } = "";
    public string afterNextStopId { get; set; } = "";
    public string afterNextStopName { get; set; } = "";
    public string nextStopScheduledDepartureTime { get; set; } = "";
    public string GpsLat { get; set; } = "";
    public string GpsLon { get; set; } = "";
    public string currentDelay { get; set; } = "";
    public string connectionId { get; set; } = "";
    public string airConditioning { get; set; } = "";
    public string onStation { get; set; } = "";
    public string nextStopIsFinal { get; set; } = "";
}