﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments.Lightning.Charge;
using BTCPayServer.Payments.Lightning.CLightning;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Payments.Lightning
{
    public class LightningLikePaymentHandler : PaymentMethodHandlerBase<LightningSupportedPaymentMethod>
    {
        NBXplorerDashboard _Dashboard;
        LightningClientFactory _LightningClientFactory;
        public LightningLikePaymentHandler(
            LightningClientFactory lightningClientFactory,
            NBXplorerDashboard dashboard)
        {
            _LightningClientFactory = lightningClientFactory;
            _Dashboard = dashboard;
        }
        public override async Task<IPaymentMethodDetails> CreatePaymentMethodDetails(LightningSupportedPaymentMethod supportedPaymentMethod, PaymentMethod paymentMethod, StoreData store, BTCPayNetwork network)
        {
            var storeBlob = store.GetStoreBlob();
            var test = Test(supportedPaymentMethod, network);
            var invoice = paymentMethod.ParentEntity;
            var due = Extensions.RoundUp(invoice.ProductInformation.Price / paymentMethod.Rate, 8);
            var client = _LightningClientFactory.CreateClient(supportedPaymentMethod, network);
            var expiry = invoice.ExpirationTime - DateTimeOffset.UtcNow;
            if (expiry < TimeSpan.Zero)
                expiry = TimeSpan.FromSeconds(1);

            LightningInvoice lightningInvoice = null;
            try
            {
                string description = storeBlob.LightningDescriptionTemplate;
                description = description.Replace("{StoreName}", store.StoreName ?? "", StringComparison.OrdinalIgnoreCase)
                                         .Replace("{ItemDescription}", invoice.ProductInformation.ItemDesc ?? "", StringComparison.OrdinalIgnoreCase)
                                         .Replace("{OrderId}", invoice.OrderId ?? "", StringComparison.OrdinalIgnoreCase);
                lightningInvoice = await client.CreateInvoice(new LightMoney(due, LightMoneyUnit.BTC), description, expiry);
            }
            catch (Exception ex)
            {
                throw new PaymentMethodUnavailableException($"Impossible to create lightning invoice ({ex.Message})", ex);
            }
            var nodeInfo = await test;
            return new LightningLikePaymentMethodDetails()
            {
                BOLT11 = lightningInvoice.BOLT11,
                InvoiceId = lightningInvoice.Id,
                NodeInfo = nodeInfo.ToString()
            };
        }

        public async Task<NodeInfo> Test(LightningSupportedPaymentMethod supportedPaymentMethod, BTCPayNetwork network)
        {
            if (!_Dashboard.IsFullySynched(network.CryptoCode, out var summary))
                throw new PaymentMethodUnavailableException($"Full node not available");

            var cts = new CancellationTokenSource(5000);
            var client = _LightningClientFactory.CreateClient(supportedPaymentMethod, network);
            LightningNodeInformation info = null;
            try
            {
                info = await client.GetInfo(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new PaymentMethodUnavailableException($"The lightning node did not replied in a timely maner");
            }
            catch (Exception ex)
            {
                throw new PaymentMethodUnavailableException($"Error while connecting to the API ({ex.Message})");
            }

            if (info.Address == null)
            {
                throw new PaymentMethodUnavailableException($"No lightning node public address has been configured");
            }

            var blocksGap = Math.Abs(info.BlockHeight - summary.Status.ChainHeight);
            if (blocksGap > 10)
            {
                throw new PaymentMethodUnavailableException($"The lightning is not synched ({blocksGap} blocks)");
            }

            return new NodeInfo(info.NodeId, info.Address, info.P2PPort);
        }

        public async Task TestConnection(NodeInfo nodeInfo, CancellationToken cancellation)
        {
            try
            {
                IPAddress address = null;
                try
                {
                    address = IPAddress.Parse(nodeInfo.Host);
                }
                catch
                {
                    address = (await Dns.GetHostAddressesAsync(nodeInfo.Host)).FirstOrDefault();
                }

                if (address == null)
                    throw new PaymentMethodUnavailableException($"DNS did not resolved {nodeInfo.Host}");

                using (var tcp = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
                {
                    await WithTimeout(tcp.ConnectAsync(new IPEndPoint(address, nodeInfo.Port)), cancellation);
                }
            }
            catch (Exception ex)
            {
                throw new PaymentMethodUnavailableException($"Error while connecting to the lightning node via {nodeInfo.Host}:{nodeInfo.Port} ({ex.Message})");
            }
        }

        static Task WithTimeout(Task task, CancellationToken token)
        {
            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
            var registration = token.Register(() => { try { tcs.TrySetResult(true); } catch { } });
#pragma warning disable CA2008 // Do not create tasks without passing a TaskScheduler
            var timeoutTask = tcs.Task;
#pragma warning restore CA2008 // Do not create tasks without passing a TaskScheduler
            return Task.WhenAny(task, timeoutTask).Unwrap().ContinueWith(t => registration.Dispose(), TaskScheduler.Default);
        }
    }
}
