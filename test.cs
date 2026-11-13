using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Moq;
// TODO: adjust these usings to match your solution
using RIM.Common;
using RIM.Core.OrderService.BL.SG.Implementation;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using Xunit;
using OrderType = RIM.Common.Common.OrderType;

namespace RIM.Core.OrderService.BL.SG.Tests
{
    /// <summary>
    /// Unit tests for RIM.Core.OrderService.BL.SG.Implementation.NotificationService
    /// Covers: GetUTAccountNumber, GetRSPUTAccountNumber, and (via those) the private
    /// GetAccountNoFromOrderType and GetCPFAccountNo.
    /// </summary>
    public class NotificationServiceTests
    {
        private const string CasaAcc = "CASA-111";
        private const string SrsAcc = "SRS-222";
        private const string CpfOaAcc = "CPFOA-333";
        private const string CpfSaAcc = "CPFSA-444";
        private const string TxnNo = "TXN001";

        private readonly Mock<IConfiguration> _configMock = new();
        private readonly Mock<IOrderRepository> _orderRepoMock = new();
        private readonly Mock<IBWCEnquiryService> _enquiryServiceMock = new();
        private readonly Mock<IDistributedCache> _distributedCacheMock = new();
        private readonly Mock<IALTEnquiryService> _altEnquiryServiceMock = new();
        private readonly Mock<ICacheEngine> _cacheMock = new();

        private readonly NotificationService _sut;

        public NotificationServiceTests()
        {
            // TODO: if the base ctor null-checks GenericMethod, replace null! with a real/mocked instance
            // (e.g. new Mock<GenericMethod>(...).Object if the class is mockable)
            _sut = new NotificationService(
                _configMock.Object,
                _orderRepoMock.Object,
                _enquiryServiceMock.Object,
                _distributedCacheMock.Object,
                _altEnquiryServiceMock.Object,
                null!,
                _cacheMock.Object);
        }

        private void VerifyNoDependencyCalls()
        {
            _configMock.VerifyNoOtherCalls();
            _orderRepoMock.VerifyNoOtherCalls();
            _enquiryServiceMock.VerifyNoOtherCalls();
            _distributedCacheMock.VerifyNoOtherCalls();
            _altEnquiryServiceMock.VerifyNoOtherCalls();
            _cacheMock.VerifyNoOtherCalls();
        }

        // ---------- dependency interaction tests ----------

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_DoesNotCallAnyInjectedDependency(string type)
        {
            var order = BuildOrder(type, "CASA", BuildPayment());

            _sut.GetUTAccountNumber(order, TxnNo);

            VerifyNoDependencyCalls();
        }

        [Fact]
        public void GetRSPUTAccountNumber_DoesNotCallAnyInjectedDependency()
        {
            var order = new UTOrder
            {
                RISInstruction = new() { PaymentMethod = KeyMappedToTrn(), Payment = BuildPayment() }
            };

            _sut.GetRSPUTAccountNumber(order, TxnNo);

            VerifyNoDependencyCalls();
        }

        // ---------- helpers ----------

        private static UTOMSPayment BuildPayment() => new()
        {
            CASAPayment = new() { UOBDebitingACNo = CasaAcc },
            SRSPayment = new() { SRSACNo = SrsAcc },
            CPFPayment = new() { CPFInvestmentACNo = CpfOaAcc, CPFACNo = CpfSaAcc }
        };

        // Only the branch matching orderType is populated, so tests also prove
        // the other branch is never touched.
        private static UTOrder BuildOrder(string type, string paymentMethod,
            UTOMSPayment payment, string investmentType = null)
        {
            var order = new UTOrder { OrderType = type, InvestmentType = investmentType };
            if (type == OrderType.Subscription)
                order.Subscription = new() { Payment = payment, PaymentMethod = paymentMethod };
            else if (type == OrderType.RIS)
                order.RISInstruction = new() { Payment = payment, PaymentMethod = paymentMethod };
            return order;
        }

        public static IEnumerable<object[]> SupportedOrderTypes()
        {
            yield return new object[] { OrderType.Subscription };
            yield return new object[] { OrderType.RIS };
        }

        // OrderType is a static class of const strings, so "unsupported" means any other string.
        public static IEnumerable<object[]> UnsupportedOrderTypes()
        {
            yield return new object[] { "UNKNOWN" };
            yield return new object[] { "" };
            yield return new object[] { " " };
            yield return new object[] { null };
            // near-misses of the supported constants must not match
            yield return new object[] { OrderType.Subscription + "X" };
            yield return new object[] { OrderType.RIS + "X" };
        }

        // =====================================================================
        // GetUTAccountNumber
        // =====================================================================

        [Theory]
        [MemberData(nameof(UnsupportedOrderTypes))]
        public void GetUTAccountNumber_UnsupportedOrderType_ReturnsEmptyString(string type)
        {
            var order = new UTOrder { OrderType = type };

            var result = _sut.GetUTAccountNumber(order, TxnNo);

            Assert.Equal(string.Empty, result);
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CASA_ReturnsUOBDebitingAcNo(string type)
        {
            var order = BuildOrder(type, "CASA", BuildPayment());

            Assert.Equal(CasaAcc, _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_SRS_ReturnsSrsAcNo(string type)
        {
            var order = BuildOrder(type, "SRS", BuildPayment());

            Assert.Equal(SrsAcc, _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CPF_InvestmentTypeCPFOA_ReturnsCpfInvestmentAcNo(string type)
        {
            var order = BuildOrder(type, "CPF", BuildPayment(), "CPFOA");

            Assert.Equal(CpfOaAcc, _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CPF_InvestmentTypeCPFSA_ReturnsCpfAcNo(string type)
        {
            var order = BuildOrder(type, "CPF", BuildPayment(), "CPFSA");

            Assert.Equal(CpfSaAcc, _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CPF_UnknownInvestmentType_FallsBackToInvestmentAcNo(string type)
        {
            var order = BuildOrder(type, "CPF", BuildPayment(), "SOMETHING_ELSE");

            Assert.Equal(CpfOaAcc, _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CPF_NullInvestmentType_FallsBackToInvestmentAcNo(string type)
        {
            var order = BuildOrder(type, "CPF", BuildPayment(), null);

            Assert.Equal(CpfOaAcc, _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CPF_InvestmentTypeIsCaseSensitive(string type)
        {
            // "cpfsa" does not match "CPFSA" so the default (OA) branch is used
            var order = BuildOrder(type, "CPF", BuildPayment(), "cpfsa");

            Assert.Equal(CpfOaAcc, _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CPF_NullCpfAccountValue_ReturnsNull(string type)
        {
            var payment = BuildPayment();
            payment.CPFPayment.CPFInvestmentACNo = null;
            var order = BuildOrder(type, "CPF", payment, "CPFOA");

            Assert.Null(_sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CASA_NullAccountValue_ReturnsNull(string type)
        {
            var payment = BuildPayment();
            payment.CASAPayment.UOBDebitingACNo = null;
            var order = BuildOrder(type, "CASA", payment);

            Assert.Null(_sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_NullPaymentMethod_ReturnsNull(string type)
        {
            var order = BuildOrder(type, null, BuildPayment());

            Assert.Null(_sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [InlineData("CASH")]
        [InlineData("casa")]   // case sensitive
        [InlineData("Srs")]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("UNKNOWN")]
        public void GetUTAccountNumber_UnrecognisedPaymentMethod_ReturnsNull_Subscription(string method)
        {
            var order = BuildOrder(OrderType.Subscription, method, BuildPayment());

            Assert.Null(_sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [InlineData("CASH")]
        [InlineData("casa")]
        [InlineData("Srs")]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("UNKNOWN")]
        public void GetUTAccountNumber_UnrecognisedPaymentMethod_ReturnsNull_RIS(string method)
        {
            var order = BuildOrder(OrderType.RIS, method, BuildPayment());

            Assert.Null(_sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("ANY-TXN")]
        public void GetUTAccountNumber_OrderTransactionNo_DoesNotAffectResult(string txnNo)
        {
            var order = BuildOrder(OrderType.Subscription, "CASA", BuildPayment());

            Assert.Equal(CasaAcc, _sut.GetUTAccountNumber(order, txnNo));
        }

        [Fact]
        public void GetUTAccountNumber_NullOrder_Throws()
        {
            Assert.Throws<NullReferenceException>(() => _sut.GetUTAccountNumber(null, TxnNo));
        }

        [Fact]
        public void GetUTAccountNumber_Subscription_NullSubscription_Throws()
        {
            var order = new UTOrder { OrderType = OrderType.Subscription, Subscription = null };

            Assert.Throws<NullReferenceException>(() => _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Fact]
        public void GetUTAccountNumber_RIS_NullRISInstruction_Throws()
        {
            var order = new UTOrder { OrderType = OrderType.RIS, RISInstruction = null };

            Assert.Throws<NullReferenceException>(() => _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CASA_NullPaymentObject_Throws(string type)
        {
            var order = BuildOrder(type, "CASA", null);

            Assert.Throws<NullReferenceException>(() => _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CASA_NullCasaPayment_Throws(string type)
        {
            var payment = BuildPayment();
            payment.CASAPayment = null;
            var order = BuildOrder(type, "CASA", payment);

            Assert.Throws<NullReferenceException>(() => _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_SRS_NullSrsPayment_Throws(string type)
        {
            var payment = BuildPayment();
            payment.SRSPayment = null;
            var order = BuildOrder(type, "SRS", payment);

            Assert.Throws<NullReferenceException>(() => _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CPF_NullCpfPayment_Throws(string type)
        {
            var payment = BuildPayment();
            payment.CPFPayment = null;
            var order = BuildOrder(type, "CPF", payment, "CPFOA");

            Assert.Throws<NullReferenceException>(() => _sut.GetUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [MemberData(nameof(SupportedOrderTypes))]
        public void GetUTAccountNumber_CASA_DoesNotTouchOtherPaymentTypes(string type)
        {
            // SRS and CPF payments are null; CASA path must not access them
            var payment = new UTOMSPayment { CASAPayment = new() { UOBDebitingACNo = CasaAcc } };
            var order = BuildOrder(type, "CASA", payment);

            Assert.Equal(CasaAcc, _sut.GetUTAccountNumber(order, TxnNo));
        }

        // =====================================================================
        // GetRSPUTAccountNumber
        // =====================================================================

        // Resolve keys from the real mapping so tests don't hard-code lookup data.
        private static string KeyMappedToTrn() =>
            RimLookupConstants.PaymentMethodMapping
                .First(kv => kv.Value == Payment_Method.TRN.ToString()).Key;

        private static string KeyNotMappedToTrn() =>
            RimLookupConstants.PaymentMethodMapping
                .First(kv => kv.Value != Payment_Method.TRN.ToString()).Key;

        private static UTOrder BuildRspOrder(string paymentMethod, UTOMSPayment payment) =>
            new()
            {
                RISInstruction = new() { PaymentMethod = paymentMethod, Payment = payment }
            };

        [Fact]
        public void GetRSPUTAccountNumber_TrnPaymentMethod_ReturnsCasaDebitingAcNo()
        {
            var order = BuildRspOrder(KeyMappedToTrn(), BuildPayment());

            Assert.Equal(CasaAcc, _sut.GetRSPUTAccountNumber(order, TxnNo));
        }

        [Fact]
        public void GetRSPUTAccountNumber_TrnPaymentMethod_NullAccountValue_ReturnsNull()
        {
            var payment = BuildPayment();
            payment.CASAPayment.UOBDebitingACNo = null;
            var order = BuildRspOrder(KeyMappedToTrn(), payment);

            Assert.Null(_sut.GetRSPUTAccountNumber(order, TxnNo));
        }

        [Fact]
        public void GetRSPUTAccountNumber_NonTrnMappedPaymentMethod_ReturnsNull()
        {
            var order = BuildRspOrder(KeyNotMappedToTrn(), BuildPayment());

            Assert.Null(_sut.GetRSPUTAccountNumber(order, TxnNo));
        }

        [Theory]
        [InlineData("NOT_IN_MAPPING")]
        [InlineData("")]
        [InlineData(null)]
        public void GetRSPUTAccountNumber_UnmappedOrNullPaymentMethod_ReturnsNull(string method)
        {
            var order = BuildRspOrder(method, BuildPayment());

            Assert.Null(_sut.GetRSPUTAccountNumber(order, TxnNo));
        }

        [Fact]
        public void GetRSPUTAccountNumber_NonTrn_DoesNotAccessPayment()
        {
            // Payment is null; non-TRN path must not dereference it
            var order = BuildRspOrder(KeyNotMappedToTrn(), null);

            Assert.Null(_sut.GetRSPUTAccountNumber(order, TxnNo));
        }

        [Fact]
        public void GetRSPUTAccountNumber_Trn_NullPayment_Throws()
        {
            var order = BuildRspOrder(KeyMappedToTrn(), null);

            Assert.Throws<NullReferenceException>(() => _sut.GetRSPUTAccountNumber(order, TxnNo));
        }

        [Fact]
        public void GetRSPUTAccountNumber_Trn_NullCasaPayment_Throws()
        {
            var payment = BuildPayment();
            payment.CASAPayment = null;
            var order = BuildRspOrder(KeyMappedToTrn(), payment);

            Assert.Throws<NullReferenceException>(() => _sut.GetRSPUTAccountNumber(order, TxnNo));
        }

        [Fact]
        public void GetRSPUTAccountNumber_NullRISInstruction_Throws()
        {
            var order = new UTOrder { RISInstruction = null };

            Assert.Throws<NullReferenceException>(() => _sut.GetRSPUTAccountNumber(order, TxnNo));
        }

        [Fact]
        public void GetRSPUTAccountNumber_NullOrder_Throws()
        {
            Assert.Throws<NullReferenceException>(() => _sut.GetRSPUTAccountNumber(null, TxnNo));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("ANY-TXN")]
        public void GetRSPUTAccountNumber_OrderTransactionNo_DoesNotAffectResult(string txnNo)
        {
            var order = BuildRspOrder(KeyMappedToTrn(), BuildPayment());

            Assert.Equal(CasaAcc, _sut.GetRSPUTAccountNumber(order, txnNo));
        }
    }
}