﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Smartstore.Core.Catalog;
using Smartstore.Core.Catalog.Attributes;
using Smartstore.Core.Catalog.Discounts;
using Smartstore.Core.Catalog.Pricing;
using Smartstore.Core.Catalog.Products;
using Smartstore.Core.Checkout.Attributes;
using Smartstore.Core.Checkout.Cart;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Checkout.Shipping;
using Smartstore.Core.Checkout.Tax;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Common.Settings;
using Smartstore.Core.Content.Media;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Localization;
using Smartstore.Core.Logging;
using Smartstore.Core.Security;
using Smartstore.Core.Seo;
using Smartstore.Core.Stores;
using Smartstore.Utilities.Html;
using Smartstore.Web.Models.Media;
using Smartstore.Web.Models.ShoppingCart;

namespace Smartstore.Web.Controllers
{
    public class ShoppingCartController : PublicControllerBase
    {
        private readonly SmartDbContext _db;
        private readonly ITaxService _taxService;
        private readonly IMediaService _mediaService;
        private readonly IActivityLogger _activityLogger;
        private readonly IPaymentService _paymentService;
        private readonly ICurrencyService _currencyService;
        private readonly IDiscountService _discountService;
        private readonly IShoppingCartService _shoppingCartService;
        private readonly ILocalizationService _localizationService;
        private readonly IDeliveryTimeService _deliveryTimeService;
        private readonly IPriceCalculationService _priceCalculationService;
        private readonly IOrderCalculationService _orderCalculationService;
        private readonly IShoppingCartValidator _shoppingCartValidator;
        private readonly IProductAttributeFormatter _productAttributeFormatter;
        private readonly ICheckoutAttributeFormatter _checkoutAttributeFormatter;
        private readonly IProductAttributeMaterializer _productAttributeMaterializer;
        private readonly ICheckoutAttributeMaterializer _checkoutAttributeMaterializer;
        private readonly ProductUrlHelper _productUrlHelper;
        private readonly ShoppingCartSettings _shoppingCartSettings;
        private readonly CatalogSettings _catalogSettings;
        private readonly MeasureSettings _measureSettings;
        private readonly OrderSettings _orderSettings;
        private readonly MediaSettings _mediaSettings;
        private readonly ShippingSettings _shippingSettings;
        private readonly RewardPointsSettings _rewardPointsSettings;

        public ShoppingCartController(
            SmartDbContext db,
            ITaxService taxService,
            IMediaService mediaService,
            IActivityLogger activityLogger,
            IPaymentService paymentService,
            ICurrencyService currencyService,
            IDiscountService discountService,
            IShoppingCartService shoppingCartService,
            ILocalizationService localizationService,
            IDeliveryTimeService deliveryTimeService,
            IPriceCalculationService priceCalculationService,
            IOrderCalculationService orderCalculationService,
            IShoppingCartValidator shoppingCartValidator,
            IProductAttributeFormatter productAttributeFormatter,
            ICheckoutAttributeFormatter checkoutAttributeFormatter,
            IProductAttributeMaterializer productAttributeMaterializer,
            ICheckoutAttributeMaterializer checkoutAttributeMaterializer,
            ProductUrlHelper productUrlHelper,
            ShoppingCartSettings shoppingCartSettings,
            CatalogSettings catalogSettings,
            MeasureSettings measureSettings,
            OrderSettings orderSettings,
            MediaSettings mediaSettings,
            ShippingSettings shippingSettings,
            RewardPointsSettings rewardPointsSettings)
        {
            _db = db;
            _taxService = taxService;
            _mediaService = mediaService;
            _activityLogger = activityLogger;
            _paymentService = paymentService;
            _currencyService = currencyService;
            _discountService = discountService;
            _shoppingCartService = shoppingCartService;
            _localizationService = localizationService;
            _deliveryTimeService = deliveryTimeService;
            _priceCalculationService = priceCalculationService;
            _orderCalculationService = orderCalculationService;
            _shoppingCartValidator = shoppingCartValidator;
            _productAttributeFormatter = productAttributeFormatter;
            _checkoutAttributeFormatter = checkoutAttributeFormatter;
            _productAttributeMaterializer = productAttributeMaterializer;
            _checkoutAttributeMaterializer = checkoutAttributeMaterializer;
            _productUrlHelper = productUrlHelper;
            _shoppingCartSettings = shoppingCartSettings;
            _catalogSettings = catalogSettings;
            _measureSettings = measureSettings;
            _orderSettings = orderSettings;
            _mediaSettings = mediaSettings;
            _shippingSettings = shippingSettings;
            _rewardPointsSettings = rewardPointsSettings;
        }

        #region Utilities

        [NonAction]
        protected async Task PrepareButtonPaymentMethodModelAsync(ButtonPaymentMethodModel model, IList<OrganizedShoppingCartItem> cart)
        {
            // TODO: (ms) (core) There was no throwing in the original code. I suggest to return if model is null or query IsNullOrEmpty().
            // Answer: Model / Cart should never be null. The internal/protected methods are only accessed within ShoppingCartController
            // and the caller instaniates model in this case, so it cannot be null.
            // However, in further development this method could be accessed differently; to ensure integrity, check the objects for null.
            // This applies also to other Guard checks in ShoppingCartController

            Guard.NotNull(model, nameof(model));
            Guard.NotNull(cart, nameof(cart));

            model.Items.Clear();

            var paymentTypes = new PaymentMethodType[] { PaymentMethodType.Button, PaymentMethodType.StandardAndButton };

            var boundPaymentMethods = await _paymentService.LoadActivePaymentMethodsAsync(
                Services.WorkContext.CurrentCustomer,
                cart,
                Services.StoreContext.CurrentStore.Id,
                paymentTypes,
                false);

            foreach (var paymentMethod in boundPaymentMethods)
            {
                if (cart.IncludesMatchingItems(x => x.IsRecurring) && paymentMethod.Value.RecurringPaymentType == RecurringPaymentType.NotSupported)
                    continue;

                var widgetInvoker = paymentMethod.Value.GetPaymentInfoWidget();
                model.Items.Add(widgetInvoker);
            }
        }

        [NonAction]
        protected async Task ParseAndSaveCheckoutAttributesAsync(List<OrganizedShoppingCartItem> cart, ProductVariantQuery query)
        {
            Guard.NotNull(cart, nameof(cart));
            Guard.NotNull(query, nameof(query));

            var selectedAttributes = new CheckoutAttributeSelection(string.Empty);
            var customer = cart.GetCustomer() ?? Services.WorkContext.CurrentCustomer;

            var checkoutAttributes = await _checkoutAttributeMaterializer.GetValidCheckoutAttributesAsync(cart);

            foreach (var attribute in checkoutAttributes)
            {
                var selectedItems = query.CheckoutAttributes.Where(x => x.AttributeId == attribute.Id);

                switch (attribute.AttributeControlType)
                {
                    case AttributeControlType.DropdownList:
                    case AttributeControlType.RadioList:
                    case AttributeControlType.Boxes:
                        {
                            var selectedValue = selectedItems.FirstOrDefault()?.Value;
                            if (selectedValue.HasValue())
                            {
                                var selectedAttributeValueId = selectedValue.SplitSafe(",").FirstOrDefault()?.ToInt();
                                if (selectedAttributeValueId.GetValueOrDefault() > 0)
                                {
                                    selectedAttributes.AddAttributeValue(attribute.Id, selectedAttributeValueId.Value);
                                }
                            }
                        }
                        break;

                    case AttributeControlType.Checkboxes:
                        {
                            foreach (var item in selectedItems)
                            {
                                var selectedValue = item.Value.SplitSafe(",").FirstOrDefault()?.ToInt();
                                if (selectedValue.GetValueOrDefault() > 0)
                                {
                                    selectedAttributes.AddAttributeValue(attribute.Id, selectedValue);
                                }
                            }
                        }
                        break;

                    case AttributeControlType.TextBox:
                    case AttributeControlType.MultilineTextbox:
                        {
                            var selectedValue = string.Join(",", selectedItems.Select(x => x.Value));
                            if (selectedValue.HasValue())
                            {
                                selectedAttributes.AddAttributeValue(attribute.Id, selectedValue);
                            }
                        }
                        break;

                    case AttributeControlType.Datepicker:
                        {
                            var selectedValue = selectedItems.FirstOrDefault()?.Date;
                            if (selectedValue.HasValue)
                            {
                                selectedAttributes.AddAttributeValue(attribute.Id, selectedValue.Value);
                            }
                        }
                        break;

                    case AttributeControlType.FileUpload:
                        {
                            var selectedValue = string.Join(",", selectedItems.Select(x => x.Value));
                            if (selectedValue.HasValue())
                            {
                                selectedAttributes.AddAttributeValue(attribute.Id, selectedValue);
                            }
                        }
                        break;
                }
            }

            customer.GenericAttributes.CheckoutAttributes = selectedAttributes;
            _db.TryUpdate(customer);
            await _db.SaveChangesAsync();
        }

        // TODO: (ms) (core) Add methods dev documentations
        [NonAction]
        protected async Task<WishlistModel> PrepareWishlistModelAsync(IList<OrganizedShoppingCartItem> cart, bool isEditable = true)
        {
            Guard.NotNull(cart, nameof(cart));

            var model = new WishlistModel
            {
                IsEditable = isEditable,
                EmailWishlistEnabled = _shoppingCartSettings.EmailWishlistEnabled,
                DisplayAddToCart = await Services.Permissions.AuthorizeAsync(Permissions.Cart.AccessShoppingCart)
            };

            if (cart.Count == 0)
                return model;

            var customer = cart.FirstOrDefault().Item.Customer;
            model.CustomerGuid = customer.CustomerGuid;
            model.CustomerFullname = customer.GetFullName();
            model.ShowItemsFromWishlistToCartButton = _shoppingCartSettings.ShowItemsFromWishlistToCartButton;

            PrepareCartModelBase(model);

            // Cart warnings
            var warnings = new List<string>();
            var cartIsValid = await _shoppingCartValidator.ValidateCartItemsAsync(cart, warnings);
            if (!cartIsValid)
            {
                model.Warnings.AddRange(warnings);
            }

            var modelItems = new List<WishlistModel.WishlistItemModel>();

            foreach (var item in cart)
            {
                modelItems.Add(await PrepareWishlistItemModelAsync(item));
            }

            (model.Items as IList<WishlistModel.WishlistItemModel>).AddRange(modelItems);

            model.Items.Each(async x =>
            {
                // Do not display QuantityUnitName in OffCanvasWishlist
                x.QuantityUnitName = null;

                var item = cart.Where(c => c.Item.Id == x.Id).FirstOrDefault();

                if (item != null)
                {
                    x.AttributeInfo = await _productAttributeFormatter.FormatAttributesAsync(
                        item.Item.AttributeSelection,
                        item.Item.Product,
                        null,
                        htmlEncode: false,
                        separator: ", ",
                        includePrices: false,
                        includeGiftCardAttributes: false,
                        includeHyperlinks: false);
                }
            });

            return model;
        }


        [NonAction]
        protected void PrepareCartModelBase(CartModelBase model)
        {
            Guard.NotNull(model, nameof(model));

            model.DisplayShortDesc = _shoppingCartSettings.ShowShortDesc;
            model.ShowProductImages = _shoppingCartSettings.ShowProductImagesOnShoppingCart;
            model.ShowProductBundleImages = _shoppingCartSettings.ShowProductBundleImagesOnShoppingCart;
            model.ShowSku = _catalogSettings.ShowProductSku;
            model.BundleThumbSize = _mediaSettings.CartThumbBundleItemPictureSize;
        }

        /// <summary>
        /// Prepares shopping cart model.
        /// </summary>
        /// <param name="model">Model instance.</param>
        /// <param name="cart">Shopping cart items.</param>
        /// <param name="isEditable">A value indicating whether the cart is editable.</param>
        /// <param name="validateCheckoutAttributes">A value indicating whether checkout attributes get validated.</param>
        /// <param name="prepareEstimateShippingIfEnabled">A value indicating whether to prepare "Estimate shipping" model.</param>
        /// <param name="setEstimateShippingDefaultAddress">A value indicating whether to prefill "Estimate shipping" model with the default customer address.</param>
        /// <param name="prepareAndDisplayOrderReviewData">A value indicating whether to prepare review data (such as billing/shipping address, payment or shipping data entered during checkout).</param>
        [NonAction]
        protected async Task<ShoppingCartModel> PrepareShoppingCartModelAsync(
            IList<OrganizedShoppingCartItem> cart,
            bool isEditable = true,
            bool validateCheckoutAttributes = false,
            bool prepareEstimateShippingIfEnabled = true,
            bool setEstimateShippingDefaultAddress = true,
            bool prepareAndDisplayOrderReviewData = false)
        {
            Guard.NotNull(cart, nameof(cart));

            if (cart.Count == 0)
            {
                return new();
            }

            var store = Services.StoreContext.CurrentStore;
            var customer = Services.WorkContext.CurrentCustomer;
            var currency = Services.WorkContext.WorkingCurrency;

            #region Simple properties

            var model = new ShoppingCartModel
            {
                MediaDimensions = _mediaSettings.CartThumbPictureSize,
                DeliveryTimesPresentation = _shoppingCartSettings.DeliveryTimesInShoppingCart,
                DisplayBasePrice = _shoppingCartSettings.ShowBasePrice,
                DisplayWeight = _shoppingCartSettings.ShowWeight,
                DisplayMoveToWishlistButton = await Services.Permissions.AuthorizeAsync(Permissions.Cart.AccessWishlist),
                TermsOfServiceEnabled = _orderSettings.TermsOfServiceEnabled,
                DisplayCommentBox = _shoppingCartSettings.ShowCommentBox,
                DisplayEsdRevocationWaiverBox = _shoppingCartSettings.ShowEsdRevocationWaiverBox,
                IsEditable = isEditable
            };

            PrepareCartModelBase(model);

            var measure = await _db.MeasureWeights.FindByIdAsync(_measureSettings.BaseWeightId, false);
            if (measure != null)
            {
                model.MeasureUnitName = measure.GetLocalized(x => x.Name);
            }

            model.CheckoutAttributeInfo = HtmlUtils.ConvertPlainTextToTable(
                HtmlUtils.ConvertHtmlToPlainText(
                    await _checkoutAttributeFormatter.FormatAttributesAsync(customer.GenericAttributes.CheckoutAttributes, customer))
                );

            // Gift card and gift card boxes.
            model.DiscountBox.Display = _shoppingCartSettings.ShowDiscountBox;
            var discountCouponCode = customer.GenericAttributes.DiscountCouponCode;
            var discount = await _db.Discounts
                .AsNoTracking()
                .Where(x => x.CouponCode == discountCouponCode)
                .FirstOrDefaultAsync();

            if (discount != null
                && discount.RequiresCouponCode
                && await _discountService.IsDiscountValidAsync(discount, customer))
            {
                model.DiscountBox.CurrentCode = discount.CouponCode;
            }

            model.GiftCardBox.Display = _shoppingCartSettings.ShowGiftCardBox;

            // Reward points.
            if (_rewardPointsSettings.Enabled && !cart.IncludesMatchingItems(x => x.IsRecurring) && !customer.IsGuest())
            {
                var rewardPointsBalance = customer.GetRewardPointsBalance();
                var rewardPointsAmountBase = _orderCalculationService.ConvertRewardPointsToAmount(rewardPointsBalance);
                var rewardPointsAmount = _currencyService.ConvertFromPrimaryCurrency(rewardPointsAmountBase.Amount, currency);

                if (rewardPointsAmount > decimal.Zero)
                {
                    model.RewardPoints.DisplayRewardPoints = true;
                    model.RewardPoints.RewardPointsAmount = rewardPointsAmount.ToString(true);
                    model.RewardPoints.RewardPointsBalance = rewardPointsBalance;
                    model.RewardPoints.UseRewardPoints = customer.GenericAttributes.UseRewardPointsDuringCheckout;
                }
            }

            // Cart warnings.
            var warnings = new List<string>();
            var cartIsValid = await _shoppingCartValidator.ValidateCartItemsAsync(cart, warnings, validateCheckoutAttributes, customer.GenericAttributes.CheckoutAttributes);
            if (!cartIsValid)
            {
                model.Warnings.AddRange(warnings);
            }

            #endregion

            #region Checkout attributes

            var checkoutAttributes = await _checkoutAttributeMaterializer.GetValidCheckoutAttributesAsync(cart);

            foreach (var attribute in checkoutAttributes)
            {
                var caModel = new ShoppingCartModel.CheckoutAttributeModel
                {
                    Id = attribute.Id,
                    Name = attribute.GetLocalized(x => x.Name),
                    TextPrompt = attribute.GetLocalized(x => x.TextPrompt),
                    IsRequired = attribute.IsRequired,
                    AttributeControlType = attribute.AttributeControlType
                };

                if (attribute.IsListTypeAttribute)
                {
                    var caValues = await _db.CheckoutAttributeValues
                        .AsNoTracking()
                        .Where(x => x.CheckoutAttributeId == attribute.Id)
                        .ToListAsync();

                    // Prepare each attribute with image and price
                    foreach (var caValue in caValues)
                    {
                        var pvaValueModel = new ShoppingCartModel.CheckoutAttributeValueModel
                        {
                            Id = caValue.Id,
                            Name = caValue.GetLocalized(x => x.Name),
                            IsPreSelected = caValue.IsPreSelected,
                            Color = caValue.Color
                        };

                        if (caValue.MediaFileId.HasValue && caValue.MediaFile != null)
                        {
                            pvaValueModel.ImageUrl = _mediaService.GetUrl(caValue.MediaFile, _mediaSettings.VariantValueThumbPictureSize, null, false);
                        }

                        caModel.Values.Add(pvaValueModel);

                        // Display price if allowed.
                        if (await Services.Permissions.AuthorizeAsync(Permissions.Catalog.DisplayPrice))
                        {
                            var priceAdjustmentBase = await _taxService.GetCheckoutAttributePriceAsync(caValue);
                            var priceAdjustment = _currencyService.ConvertFromPrimaryCurrency(priceAdjustmentBase.Price.Amount, currency);

                            if (priceAdjustmentBase.Price > decimal.Zero)
                            {
                                pvaValueModel.PriceAdjustment = "+" + priceAdjustmentBase.Price.ToString();
                            }
                            else if (priceAdjustmentBase.Price < decimal.Zero)
                            {
                                pvaValueModel.PriceAdjustment = "-" + priceAdjustmentBase.Price.ToString();
                            }
                        }
                    }
                }

                // Set already selected attributes.
                var selectedCheckoutAttributes = customer.GenericAttributes.CheckoutAttributes;
                switch (attribute.AttributeControlType)
                {
                    case AttributeControlType.DropdownList:
                    case AttributeControlType.RadioList:
                    case AttributeControlType.Boxes:
                    case AttributeControlType.Checkboxes:
                        if (selectedCheckoutAttributes.AttributesMap.Any())
                        {
                            // Clear default selection.
                            foreach (var item in caModel.Values)
                            {
                                item.IsPreSelected = false;
                            }

                            // Select new values.
                            var selectedCaValues = await _checkoutAttributeMaterializer.MaterializeCheckoutAttributeValuesAsync(selectedCheckoutAttributes);
                            foreach (var caValue in selectedCaValues)
                            {
                                foreach (var item in caModel.Values)
                                {
                                    if (caValue.Id == item.Id)
                                    {
                                        item.IsPreSelected = true;
                                    }
                                }
                            }
                        }
                        break;

                    case AttributeControlType.TextBox:
                    case AttributeControlType.MultilineTextbox:
                        if (selectedCheckoutAttributes.AttributesMap.Any())
                        {
                            var enteredText = selectedCheckoutAttributes.AttributesMap
                                .Where(x => x.Key == attribute.Id)
                                .SelectMany(x => x.Value)
                                .FirstOrDefault()
                                .ToString();

                            if (enteredText.HasValue())
                            {
                                caModel.TextValue = enteredText;
                            }
                        }
                        break;

                    case AttributeControlType.Datepicker:
                        {
                            // Keep in mind my that the code below works only in the current culture.
                            var enteredDate = selectedCheckoutAttributes.AttributesMap
                                .Where(x => x.Key == attribute.Id)
                                .SelectMany(x => x.Value)
                                .FirstOrDefault()
                                .ToString();

                            if (enteredDate.HasValue()
                                && DateTime.TryParseExact(enteredDate, "D", CultureInfo.CurrentCulture, DateTimeStyles.None, out var selectedDate))
                            {
                                caModel.SelectedDay = selectedDate.Day;
                                caModel.SelectedMonth = selectedDate.Month;
                                caModel.SelectedYear = selectedDate.Year;
                            }
                        }
                        break;

                    case AttributeControlType.FileUpload:
                        if (selectedCheckoutAttributes.AttributesMap.Any())
                        {
                            var FileValue = selectedCheckoutAttributes.AttributesMap
                                .Where(x => x.Key == attribute.Id)
                                .SelectMany(x => x.Value)
                                .FirstOrDefault()
                                .ToString();

                            if (FileValue.HasValue() && caModel.UploadedFileGuid.HasValue() && Guid.TryParse(caModel.UploadedFileGuid, out var guid))
                            {
                                var download = await _db.Downloads
                                    .Include(x => x.MediaFile)
                                    .FirstOrDefaultAsync(x => x.DownloadGuid == guid);

                                if (download != null && !download.UseDownloadUrl && download.MediaFile != null)
                                {
                                    caModel.UploadedFileName = download.MediaFile.Name;
                                }
                            }
                        }
                        break;

                    default:
                        break;
                }

                model.CheckoutAttributes.Add(caModel);
            }

            #endregion

            #region Estimate shipping

            if (prepareEstimateShippingIfEnabled)
            {
                model.EstimateShipping.Enabled = _shippingSettings.EstimateShippingEnabled && cart.Any() && cart.IncludesMatchingItems(x => x.IsShippingEnabled);
                if (model.EstimateShipping.Enabled)
                {
                    // Countries.
                    var defaultEstimateCountryId = setEstimateShippingDefaultAddress && customer.ShippingAddress != null
                        ? customer.ShippingAddress.CountryId
                        : model.EstimateShipping.CountryId;

                    var countriesForShipping = await _db.Countries
                        .AsNoTracking()
                        .ApplyStoreFilter(store.Id)
                        .Where(x => x.AllowsShipping)
                        .ToListAsync();

                    foreach (var countries in countriesForShipping)
                    {
                        model.EstimateShipping.AvailableCountries.Add(new SelectListItem
                        {
                            Text = countries.GetLocalized(x => x.Name),
                            Value = countries.Id.ToString(),
                            Selected = countries.Id == defaultEstimateCountryId
                        });
                    }

                    // States.
                    var states = defaultEstimateCountryId.HasValue
                        ? await _db.StateProvinces.AsNoTracking().ApplyCountryFilter(defaultEstimateCountryId.Value).ToListAsync()
                        : new();

                    if (states.Any())
                    {
                        var defaultEstimateStateId = setEstimateShippingDefaultAddress && customer.ShippingAddress != null
                            ? customer.ShippingAddress.StateProvinceId
                            : model.EstimateShipping.StateProvinceId;

                        foreach (var s in states)
                        {
                            model.EstimateShipping.AvailableStates.Add(new SelectListItem
                            {
                                Text = s.GetLocalized(x => x.Name),
                                Value = s.Id.ToString(),
                                Selected = s.Id == defaultEstimateStateId
                            });
                        }
                    }
                    else
                    {
                        model.EstimateShipping.AvailableStates.Add(new SelectListItem { Text = await _localizationService.GetResourceAsync("Address.OtherNonUS"), Value = "0" });
                    }

                    if (setEstimateShippingDefaultAddress && customer.ShippingAddress != null)
                    {
                        model.EstimateShipping.ZipPostalCode = customer.ShippingAddress.ZipPostalCode;
                    }
                }
            }

            #endregion

            #region Cart items

            var modelItems = new List<ShoppingCartModel.ShoppingCartItemModel>();

            foreach (var item in cart)
            {
                modelItems.Add(await PrepareShoppingCartItemModelAsync(item));
            }

            (model.Items as IList<ShoppingCartModel.ShoppingCartItemModel>).AddRange(modelItems);

            #endregion

            #region Order review data

            if (prepareAndDisplayOrderReviewData)
            {
                HttpContext.Session.TryGetObject(CheckoutState.CheckoutStateSessionKey, out CheckoutState checkoutState);

                model.OrderReviewData.Display = true;

                // Billing info.
                // TODO: (mh)(core)Implement AddressModels PrepareModel()
                //var billingAddress = customer.BillingAddress;
                //if (billingAddress != null)
                //{
                //    model.OrderReviewData.BillingAddress.PrepareModel(billingAddress, false, _addressSettings);
                //}

                // Shipping info.
                if (cart.IsShippingRequired())
                {
                    model.OrderReviewData.IsShippable = true;

                    // TODO: (mh) (core) Implement AddressModels PrepareModel()
                    //var shippingAddress = customer.ShippingAddress;
                    //if (shippingAddress != null)
                    //{
                    //    model.OrderReviewData.ShippingAddress.PrepareModel(shippingAddress, false, _addressSettings);
                    //}

                    // Selected shipping method.
                    var shippingOption = customer.GenericAttributes.SelectedShippingOption;
                    if (shippingOption != null)
                    {
                        model.OrderReviewData.ShippingMethod = shippingOption.Name;
                    }

                    if (checkoutState.CustomProperties.ContainsKey("HasOnlyOneActiveShippingMethod"))
                    {
                        model.OrderReviewData.DisplayShippingMethodChangeOption = !(bool)checkoutState.CustomProperties.Get("HasOnlyOneActiveShippingMethod");
                    }
                }

                if (checkoutState.CustomProperties.ContainsKey("HasOnlyOneActivePaymentMethod"))
                {
                    model.OrderReviewData.DisplayPaymentMethodChangeOption = !(bool)checkoutState.CustomProperties.Get("HasOnlyOneActivePaymentMethod");
                }

                var selectedPaymentMethodSystemName = customer.GenericAttributes.SelectedPaymentMethod;
                var paymentMethod = await _paymentService.LoadPaymentMethodBySystemNameAsync(selectedPaymentMethodSystemName);

                //// TODO: (ms) (core) PluginMediator.GetLocalizedFriendlyName is missing
                ////model.OrderReviewData.PaymentMethod = paymentMethod != null ? _pluginMediator.GetLocalizedFriendlyName(paymentMethod.Metadata) : "";
                //model.OrderReviewData.PaymentSummary = checkoutState.PaymentSummary;
                //model.OrderReviewData.IsPaymentSelectionSkipped = checkoutState.IsPaymentSelectionSkipped;
            }

            #endregion

            await PrepareButtonPaymentMethodModelAsync(model.ButtonPaymentMethods, cart);

            return model;
        }

        [NonAction]
        protected async Task<WishlistModel.WishlistItemModel> PrepareWishlistItemModelAsync(OrganizedShoppingCartItem cartItem)
        {
            Guard.NotNull(cartItem, nameof(cartItem));

            var item = cartItem.Item;

            var model = new WishlistModel.WishlistItemModel
            {
                DisableBuyButton = item.Product.DisableBuyButton
            };

            await PrepareCartItemModelBaseAsync(cartItem, model);

            if (cartItem.ChildItems != null)
            {
                var childList = new List<WishlistModel.WishlistItemModel>();

                foreach (var childItem in cartItem.ChildItems.Where(x => x.Item.Id != item.Id))
                {
                    var childModel = await PrepareWishlistItemModelAsync(childItem);
                    childList.Add(childModel);
                }

                model.ChildItems.ToList().Clear();
                model.ChildItems.ToList().AddRange(childList);
            }
            return model;
        }

        [NonAction]
        protected async Task<ShoppingCartModel.ShoppingCartItemModel> PrepareShoppingCartItemModelAsync(OrganizedShoppingCartItem cartItem)
        {
            Guard.NotNull(cartItem, nameof(cartItem));

            var item = cartItem.Item;
            var product = item.Product;

            var model = new ShoppingCartModel.ShoppingCartItemModel
            {
                Weight = product.Weight,
                IsShipEnabled = product.IsShippingEnabled,
                IsDownload = product.IsDownload,
                HasUserAgreement = product.HasUserAgreement,
                IsEsd = product.IsEsd,
                DisableWishlistButton = product.DisableWishlistButton,
            };

            if (product.DisplayDeliveryTimeAccordingToStock(_catalogSettings))
            {
                var deliveryTime = await _deliveryTimeService.GetDeliveryTimeAsync(product.GetDeliveryTimeIdAccordingToStock(_catalogSettings));
                if (deliveryTime != null)
                {
                    model.DeliveryTimeName = deliveryTime.GetLocalized(x => x.Name);
                    model.DeliveryTimeHexValue = deliveryTime.ColorHexValue;

                    if (_shoppingCartSettings.DeliveryTimesInShoppingCart is DeliveryTimesPresentation.DateOnly
                        or DeliveryTimesPresentation.LabelAndDate)
                    {
                        model.DeliveryTimeDate = _deliveryTimeService.GetFormattedDeliveryDate(deliveryTime);
                    }
                }
            }

            var basePriceAdjustment = (await _priceCalculationService.GetFinalPriceAsync(product, null)
                - await _priceCalculationService.GetUnitPriceAsync(cartItem, true)) * -1;

            model.BasePrice = await _priceCalculationService.GetBasePriceInfoAsync(product, item.Customer, Services.WorkContext.WorkingCurrency, basePriceAdjustment);

            await PrepareCartItemModelBaseAsync(cartItem, model);

            if (cartItem.Item.BundleItem == null)
            {
                var selectedAttributeValues = await _productAttributeMaterializer.MaterializeProductVariantAttributeValuesAsync(item.AttributeSelection);
                if (selectedAttributeValues != null)
                {
                    var weight = decimal.Zero;
                    foreach (var attributeValue in selectedAttributeValues)
                    {
                        weight += attributeValue.WeightAdjustment;
                    }

                    model.Weight += weight;
                }
            }

            if (cartItem.ChildItems != null)
            {
                var childList = new List<ShoppingCartModel.ShoppingCartItemModel>();

                foreach (var childItem in cartItem.ChildItems.Where(x => x.Item.Id != item.Id))
                {
                    var childModel = await PrepareShoppingCartItemModelAsync(childItem);
                    childList.Add(childModel);
                }

                model.ChildItems.ToList().Clear();
                model.ChildItems.ToList().AddRange(childList);
            }

            return model;
        }

        [NonAction]
        protected async Task PrepareCartItemModelBaseAsync(OrganizedShoppingCartItem cartItem, CartEntityModelBase model)
        {
            Guard.NotNull(cartItem, nameof(cartItem));

            var item = cartItem.Item;
            var product = cartItem.Item.Product;
            var customer = item.Customer;
            var currency = Services.WorkContext.WorkingCurrency;
            var shoppingCartType = item.ShoppingCartType;

            await _productAttributeMaterializer.MergeWithCombinationAsync(product, item.AttributeSelection);

            var productSeName = await product.GetActiveSlugAsync();

            // General model data
            model.Id = item.Id;
            model.Sku = product.Sku;
            model.ProductId = product.Id;
            model.ProductName = product.GetLocalized(x => x.Name);
            model.ProductSeName = productSeName;
            model.ProductUrl = await _productUrlHelper.GetProductUrlAsync(productSeName, cartItem);
            model.EnteredQuantity = item.Quantity;
            model.MinOrderAmount = product.OrderMinimumQuantity;
            model.MaxOrderAmount = product.OrderMaximumQuantity;
            model.QuantityStep = product.QuantityStep > 0 ? product.QuantityStep : 1;
            model.ShortDesc = product.GetLocalized(x => x.ShortDescription);
            model.ProductType = product.ProductType;
            model.VisibleIndividually = product.Visibility != ProductVisibility.Hidden;
            model.CreatedOnUtc = item.UpdatedOnUtc;

            if (item.BundleItem != null)
            {
                model.BundleItem.Id = item.BundleItem.Id;
                model.BundleItem.DisplayOrder = item.BundleItem.DisplayOrder;
                model.BundleItem.HideThumbnail = item.BundleItem.HideThumbnail;
                model.BundlePerItemPricing = item.BundleItem.BundleProduct.BundlePerItemPricing;
                model.BundlePerItemShoppingCart = item.BundleItem.BundleProduct.BundlePerItemShoppingCart;
                model.AttributeInfo = await _productAttributeFormatter.FormatAttributesAsync(
                    item.AttributeSelection,
                    product,
                    customer,
                    includePrices: false,
                    includeGiftCardAttributes: true,
                    includeHyperlinks: true);

                var bundleItemName = item.BundleItem.GetLocalized(x => x.Name);
                if (bundleItemName.Value.HasValue())
                {
                    model.ProductName = bundleItemName;
                }

                var bundleItemShortDescription = item.BundleItem.GetLocalized(x => x.ShortDescription);
                if (bundleItemShortDescription.Value.HasValue())
                {
                    model.ShortDesc = bundleItemShortDescription;
                }

                if (model.BundlePerItemPricing && model.BundlePerItemShoppingCart)
                {
                    var bundleItemSubTotalWithDiscountBase = await _taxService.GetProductPriceAsync(product, await _priceCalculationService.GetSubTotalAsync(cartItem, true));
                    var bundleItemSubTotalWithDiscount = _currencyService.ConvertFromPrimaryCurrency(bundleItemSubTotalWithDiscountBase.Price.Amount, currency);
                    model.BundleItem.PriceWithDiscount = bundleItemSubTotalWithDiscount.ToString();
                }
            }
            else
            {
                model.AttributeInfo = await _productAttributeFormatter.FormatAttributesAsync(item.AttributeSelection, product, customer);
            }

            var allowedQuantities = product.ParseAllowedQuantities();
            foreach (var quantity in allowedQuantities)
            {
                model.AllowedQuantities.Add(new SelectListItem
                {
                    Text = quantity.ToString(),
                    Value = quantity.ToString(),
                    Selected = item.Quantity == quantity
                });
            }

            var quantityUnit = await _db.QuantityUnits.GetQuantityUnitByIdAsync(product.QuantityUnitId ?? 0, _catalogSettings.ShowDefaultQuantityUnit);
            if (quantityUnit != null)
            {
                model.QuantityUnitName = quantityUnit.GetLocalized(x => x.Name);
            }

            if (product.IsRecurring)
            {
                model.RecurringInfo = T("ShoppingCart.RecurringPeriod", product.RecurringCycleLength, product.RecurringCyclePeriod.GetLocalizedEnum());
            }

            if (product.CallForPrice)
            {
                model.UnitPrice = T("Products.CallForPrice");
            }
            else
            {
                var unitPriceWithDiscountBase = await _taxService.GetProductPriceAsync(product, await _priceCalculationService.GetUnitPriceAsync(cartItem, true));
                var unitPriceWithDiscount = _currencyService.ConvertFromPrimaryCurrency(unitPriceWithDiscountBase.Price.Amount, currency);
                model.UnitPrice = unitPriceWithDiscount.ToString();
            }

            // Subtotal and discount.
            if (product.CallForPrice)
            {
                model.SubTotal = T("Products.CallForPrice");
            }
            else
            {
                var cartItemSubTotalWithDiscount = await _priceCalculationService.GetSubTotalAsync(cartItem, true);
                var cartItemSubTotalWithDiscountBase = await _taxService.GetProductPriceAsync(product, cartItemSubTotalWithDiscount);
                cartItemSubTotalWithDiscount = _currencyService.ConvertFromPrimaryCurrency(cartItemSubTotalWithDiscountBase.Price.Amount, currency);

                model.SubTotal = cartItemSubTotalWithDiscount.ToString();

                // Display an applied discount amount.
                var cartItemSubTotalWithoutDiscount = await _priceCalculationService.GetSubTotalAsync(cartItem, false);
                var cartItemSubTotalWithoutDiscountBase = await _taxService.GetProductPriceAsync(product, cartItemSubTotalWithoutDiscount);
                var cartItemSubTotalDiscountBase = cartItemSubTotalWithoutDiscountBase.Price - cartItemSubTotalWithDiscountBase.Price;

                if (cartItemSubTotalDiscountBase > decimal.Zero)
                {
                    var itemDiscount = _currencyService.ConvertFromPrimaryCurrency(cartItemSubTotalDiscountBase.Amount, currency);
                    model.Discount = itemDiscount.ToString();
                }
            }

            if (item.BundleItem != null)
            {
                if (_shoppingCartSettings.ShowProductBundleImagesOnShoppingCart)
                {
                    model.Image = await PrepareCartItemImageModelAsync(product, item.AttributeSelection, _mediaSettings.CartThumbBundleItemPictureSize, model.ProductName);
                }
            }
            else
            {
                if (_shoppingCartSettings.ShowProductImagesOnShoppingCart)
                {
                    model.Image = await PrepareCartItemImageModelAsync(product, item.AttributeSelection, _mediaSettings.CartThumbPictureSize, model.ProductName);
                }
            }

            var itemWarnings = new List<string>();
            var isItemValid = await _shoppingCartValidator.ValidateCartItemsAsync(new List<OrganizedShoppingCartItem> { cartItem }, itemWarnings);
            if (!isItemValid)
            {
                itemWarnings.Each(x => model.Warnings.Add(x));
            }
        }

        [NonAction]
        protected async Task<ImageModel> PrepareCartItemImageModelAsync(
            Product product,
            ProductVariantAttributeSelection attributeSelection,
            int pictureSize,
            string productName)
        {
            Guard.NotNull(product, nameof(product));

            var combination = await _productAttributeMaterializer.FindAttributeCombinationAsync(product.Id, attributeSelection);

            MediaFileInfo file = null;
            if (combination != null)
            {
                var fileIds = combination.GetAssignedMediaIds();
                if (fileIds.Any())
                {
                    file = await _mediaService.GetFileByIdAsync(fileIds[0], MediaLoadFlags.AsNoTracking);
                }
            }

            // If no attribute combination image was found, then load product pictures.
            if (file == null)
            {
                var mediaFile = await _db.ProductMediaFiles
                    .AsNoTracking()
                    .Include(x => x.MediaFile)
                    .ApplyProductFilter(product.Id)
                    .FirstOrDefaultAsync();

                if (mediaFile?.MediaFile != null)
                {
                    file = _mediaService.ConvertMediaFile(mediaFile.MediaFile);
                }
            }

            // Let's check whether this product has some parent "grouped" product.
            if (file == null && product.Visibility == ProductVisibility.Hidden && product.ParentGroupedProductId > 0)
            {
                var mediaFile = await _db.ProductMediaFiles
                    .AsNoTracking()
                    .Include(x => x.MediaFile)
                    .ApplyProductFilter(product.ParentGroupedProductId)
                    .FirstOrDefaultAsync();

                if (mediaFile?.MediaFile != null)
                {
                    file = _mediaService.ConvertMediaFile(mediaFile.MediaFile);
                }
            }

            return new ImageModel
            {
                File = file,
                ThumbSize = pictureSize,
                Title = file?.File?.GetLocalized(x => x.Title)?.Value.NullEmpty() ?? T("Media.Product.ImageLinkTitleFormat", productName),
                Alt = file?.File?.GetLocalized(x => x.Alt)?.Value.NullEmpty() ?? T("Media.Product.ImageAlternateTextFormat", productName),
                NoFallback = _catalogSettings.HideProductDefaultPictures,
            };
        }

        [NonAction]
        protected async Task<MiniShoppingCartModel> PrepareMiniShoppingCartModelAsync()
        {
            var customer = Services.WorkContext.CurrentCustomer;
            var storeId = Services.StoreContext.CurrentStore.Id;

            var model = new MiniShoppingCartModel
            {
                ShowProductImages = _shoppingCartSettings.ShowProductImagesInMiniShoppingCart,
                ThumbSize = _mediaSettings.MiniCartThumbPictureSize,
                CurrentCustomerIsGuest = customer.IsGuest(),
                AnonymousCheckoutAllowed = _orderSettings.AnonymousCheckoutAllowed,
                DisplayMoveToWishlistButton = await Services.Permissions.AuthorizeAsync(Permissions.Cart.AccessWishlist),
                ShowBasePrice = _shoppingCartSettings.ShowBasePrice
            };

            var cart = await _shoppingCartService.GetCartItemsAsync(customer, ShoppingCartType.ShoppingCart, storeId);
            model.TotalProducts = cart.GetTotalQuantity();

            if (cart.Count == 0)
            {
                return model;
            }

            model.SubTotal = (await _orderCalculationService.GetShoppingCartSubTotalAsync(cart)).SubTotalWithoutDiscount.ToString();

            //a customer should visit the shopping cart page before going to checkout if:
            //1. we have at least one checkout attribute that is reqired
            //2. min order sub-total is OK

            var checkoutAttributes = await _checkoutAttributeMaterializer.GetValidCheckoutAttributesAsync(cart);

            model.DisplayCheckoutButton = !checkoutAttributes.Any(x => x.IsRequired);

            // Products sort descending (recently added products)
            foreach (var cartItem in cart)
            {
                var item = cartItem.Item;
                var product = cartItem.Item.Product;
                var productSeName = await product.GetActiveSlugAsync();

                var cartItemModel = new MiniShoppingCartModel.ShoppingCartItemModel
                {
                    Id = item.Id,
                    ProductId = product.Id,
                    ProductName = product.GetLocalized(x => x.Name),
                    ShortDesc = product.GetLocalized(x => x.ShortDescription),
                    ProductSeName = productSeName,
                    EnteredQuantity = item.Quantity,
                    MaxOrderAmount = product.OrderMaximumQuantity,
                    MinOrderAmount = product.OrderMinimumQuantity,
                    QuantityStep = product.QuantityStep > 0 ? product.QuantityStep : 1,
                    CreatedOnUtc = item.UpdatedOnUtc,
                    ProductUrl = await _productUrlHelper.GetProductUrlAsync(productSeName, cartItem),
                    QuantityUnitName = null,
                    AttributeInfo = await _productAttributeFormatter.FormatAttributesAsync(
                        item.AttributeSelection,
                        product,
                        null,
                        ", ",
                        false,
                        false,
                        false,
                        false,
                        false)
                };

                if (cartItem.ChildItems != null && _shoppingCartSettings.ShowProductBundleImagesOnShoppingCart)
                {
                    var bundleItems = cartItem.ChildItems.Where(x =>
                        x.Item.Id != item.Id
                        && x.Item.BundleItem != null
                        && !x.Item.BundleItem.HideThumbnail);

                    foreach (var bundleItem in bundleItems)
                    {
                        var bundleItemModel = new MiniShoppingCartModel.ShoppingCartItemBundleItem
                        {
                            ProductName = bundleItem.Item.Product.GetLocalized(x => x.Name),
                            ProductSeName = await bundleItem.Item.Product.GetActiveSlugAsync(),
                        };

                        bundleItemModel.ProductUrl = await _productUrlHelper.GetProductUrlAsync(
                            bundleItem.Item.ProductId,
                            bundleItemModel.ProductSeName,
                            bundleItem.Item.AttributeSelection);

                        var file = await _db.ProductMediaFiles
                            .AsNoTracking()
                            .Include(x => x.MediaFile)
                            .ApplyProductFilter(bundleItem.Item.ProductId)
                            .FirstOrDefaultAsync();

                        if (file?.MediaFile != null)
                        {
                            bundleItemModel.PictureUrl = _mediaService.GetUrl(file.MediaFile, MediaSettings.ThumbnailSizeXxs);
                        }

                        cartItemModel.BundleItems.Add(bundleItemModel);
                    }
                }

                // Unit prices.
                if (product.CallForPrice)
                {
                    cartItemModel.UnitPrice = await _localizationService.GetResourceAsync("Products.CallForPrice");
                }
                else
                {
                    var attributeCombination = await _productAttributeMaterializer.FindAttributeCombinationAsync(item.ProductId, item.AttributeSelection);
                    product.MergeWithCombination(attributeCombination);

                    var unitPriceWithDiscountBase = await _taxService.GetProductPriceAsync(product, await _priceCalculationService.GetUnitPriceAsync(cartItem, true));
                    var unitPriceWithDiscount = _currencyService.ConvertFromPrimaryCurrency(unitPriceWithDiscountBase.Price.Amount, Services.WorkContext.WorkingCurrency);

                    cartItemModel.UnitPrice = unitPriceWithDiscount.ToString();

                    if (unitPriceWithDiscount != decimal.Zero && model.ShowBasePrice)
                    {
                        cartItemModel.BasePriceInfo = await _priceCalculationService.GetBasePriceInfoAsync(item.Product);
                    }
                }

                // Image.
                if (_shoppingCartSettings.ShowProductImagesInMiniShoppingCart)
                {
                    cartItemModel.Image = await PrepareCartItemImageModelAsync(product, item.AttributeSelection, _mediaSettings.MiniCartThumbPictureSize, cartItemModel.ProductName);
                }

                model.Items.Add(cartItemModel);
            }

            return model;
        }

        #endregion

        public IActionResult CartSummary()
        {
            // Stop annoying MiniProfiler report.
            return new EmptyResult();
        }

        [RequireSsl]
        public async Task<IActionResult> Cart(ProductVariantQuery query)
        {
            Guard.NotNull(query, nameof(query));

            if (!await Services.Permissions.AuthorizeAsync(Permissions.Cart.AccessShoppingCart))
                return RedirectToRoute("HomePage");

            var cart = await _shoppingCartService.GetCartItemsAsync(storeId: Services.StoreContext.CurrentStore.Id);

            // Allow to fill checkout attributes with values from query string.
            if (query.CheckoutAttributes.Any())
            {
                await ParseAndSaveCheckoutAttributesAsync(cart, query);
            }

            var model = await PrepareShoppingCartModelAsync(cart);

            HttpContext.Session.TrySetObject(CheckoutState.CheckoutStateSessionKey, new CheckoutState());

            return View(model);
        }

        [RequireSsl]
        public async Task<IActionResult> Wishlist(Guid? customerGuid)
        {
            if (!await Services.Permissions.AuthorizeAsync(Permissions.Cart.AccessWishlist))
                return RedirectToRoute("HomePage");

            var customer = customerGuid.HasValue
                ? await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.CustomerGuid == customerGuid.Value)
                : Services.WorkContext.CurrentCustomer;

            if (customer == null)
            {
                return RedirectToRoute("HomePage");
            }

            var cart = await _shoppingCartService.GetCartItemsAsync(customer, ShoppingCartType.Wishlist, Services.StoreContext.CurrentStore.Id);

            var model = await PrepareWishlistModelAsync(cart, !customerGuid.HasValue);

            return View(model);
        }

        public async Task<IActionResult> OffCanvasShoppingCart()
        {
            if (!_shoppingCartSettings.MiniShoppingCartEnabled)
                return Content(string.Empty);

            if (!await Services.Permissions.AuthorizeAsync(Permissions.Cart.AccessShoppingCart))
                return Content(string.Empty);

            var model = await PrepareMiniShoppingCartModelAsync();

            HttpContext.Session.TrySetObject(CheckoutState.CheckoutStateSessionKey, new CheckoutState());

            return PartialView(model);
        }

        public async Task<IActionResult> OffCanvasWishlist()
        {
            var customer = Services.WorkContext.CurrentCustomer;
            var storeId = Services.StoreContext.CurrentStore.Id;

            var cartItems = await _shoppingCartService.GetCartItemsAsync(customer, ShoppingCartType.Wishlist, storeId);

            var model = await PrepareWishlistModelAsync(cartItems, true);

            model.ThumbSize = _mediaSettings.MiniCartThumbPictureSize;

            return PartialView(model);
        }

        #region Shopping cart

        // TODO: (ms) (core) Add dev docu to all ajax action methods
        [HttpPost]
        public async Task<IActionResult> AddProductSimple(int productId, int shoppingCartTypeId = 1, bool forceRedirection = false)
        {
            // Adds products without variants to the cart or redirects user to product details page.
            // This method is used on catalog pages (category/manufacturer etc...).

            var product = await _db.Products.FindByIdAsync(productId, false);
            if (product == null)
            {
                return Json(new
                {
                    success = false,
                    message = T("Products.NotFound", productId)
                });
            }

            // Filter out cases where a product cannot be added to the cart
            if (product.ProductType == ProductType.GroupedProduct || product.CustomerEntersPrice || product.IsGiftCard)
            {
                return Json(new
                {
                    redirect = Url.RouteUrl("Product", new { SeName = await product.GetActiveSlugAsync() }),
                });
            }

            var allowedQuantities = product.ParseAllowedQuantities();
            if (allowedQuantities.Length > 0)
            {
                // The user must select a quantity from the dropdown list, therefore the product cannot be added to the cart
                return Json(new
                {
                    redirect = Url.RouteUrl("Product", new { SeName = await product.GetActiveSlugAsync() }),
                });
            }

            // Get product warnings without attribute validations.

            var storeId = Services.StoreContext.CurrentStore.Id;
            var cartType = (ShoppingCartType)shoppingCartTypeId;

            // Get existing shopping cart items. Then, tries to find a cart item with the corresponding product.
            var cart = await _shoppingCartService.GetCartItemsAsync(null, cartType, storeId);
            var cartItem = cart.FindItemInCart(cartType, product);

            var quantityToAdd = product.OrderMinimumQuantity > 0 ? product.OrderMinimumQuantity : 1;

            // If we already have the same product in the cart, then use the total quantity to validate
            quantityToAdd = cartItem != null ? cartItem.Item.Quantity + quantityToAdd : quantityToAdd;

            var productWarnings = new List<string>();
            if (!await _shoppingCartValidator.ValidateProductAsync(cartItem.Item, productWarnings, storeId, quantityToAdd))
            {
                // Product is not valid and therefore cannot be added to the cart. Display standard product warnings.
                return Json(new
                {
                    success = false,
                    message = productWarnings.ToArray()
                });
            }

            // Product looks good so far, let's try adding the product to the cart (with product attribute validation etc.)
            var addToCartContext = new AddToCartContext
            {
                Product = product,
                CartType = cartType,
                Quantity = quantityToAdd,
                AutomaticallyAddRequiredProducts = true
            };

            if (!await _shoppingCartService.AddToCartAsync(addToCartContext))
            {
                // Item could not be added to the cart. Most likely, the customer has to select product variant attributes.
                return Json(new
                {
                    redirect = Url.RouteUrl("Product", new { SeName = await product.GetActiveSlugAsync() }),
                });
            }

            // Product has been added to the cart. Add to activity log.
            _activityLogger.LogActivity("PublicStore.AddToShoppingCart", _localizationService.GetResource("ActivityLog.PublicStore.AddToShoppingCart"), product.Name);

            if (_shoppingCartSettings.DisplayCartAfterAddingProduct || forceRedirection)
            {
                // Redirect to the shopping cart page
                return Json(new
                {
                    redirect = Url.RouteUrl("ShoppingCart"),
                });
            }

            return Json(new
            {
                success = true,
                message = string.Format(_localizationService.GetResource("Products.ProductHasBeenAddedToTheCart"), Url.RouteUrl("ShoppingCart"))
            });
        }

        [HttpPost]
        public async Task<IActionResult> AddProduct(int productId, int shoppingCartTypeId, ProductVariantQuery query, FormCollection form)
        {
            // Adds a product to cart. This method is used on product details page.

            var product = await _db.Products.FindByIdAsync(productId, false);
            if (product == null)
            {
                return Json(new
                {
                    redirect = Url.RouteUrl("HomePage"),
                });
            }

            Money customerEnteredPriceConverted = new();
            if (product.CustomerEntersPrice)
            {
                foreach (var formKey in form.Keys)
                {
                    if (formKey.Equals(string.Format("addtocart_{0}.CustomerEnteredPrice", productId), StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (decimal.TryParse(form[formKey], out var customerEnteredPrice))
                        {
                            customerEnteredPriceConverted = _currencyService.ConvertToPrimaryCurrency(new Money(customerEnteredPrice, Services.WorkContext.WorkingCurrency));
                        }

                        break;
                    }
                }
            }

            var quantity = product.OrderMinimumQuantity;
            var key1 = "addtocart_{0}.EnteredQuantity".FormatWith(productId);
            var key2 = "addtocart_{0}.AddToCart.EnteredQuantity".FormatWith(productId);

            if (form.Keys.Contains(key1))
            {
                _ = int.TryParse(form[key1], out quantity);
            }
            else if (form.Keys.Contains(key2))
            {
                _ = int.TryParse(form[key2], out quantity);
            }

            // Save item
            var cartType = (ShoppingCartType)shoppingCartTypeId;

            var addToCartContext = new AddToCartContext
            {
                Product = product,
                VariantQuery = query,
                CartType = cartType,
                CustomerEnteredPrice = customerEnteredPriceConverted,
                Quantity = quantity,
                AutomaticallyAddRequiredProducts = true
            };

            if (!await _shoppingCartService.AddToCartAsync(addToCartContext))
            {
                // Product could not be added to the cart/wishlist
                // Display warnings.
                return Json(new
                {
                    success = false,
                    message = addToCartContext.Warnings.ToArray()
                });
            }



            // Product was successfully added to the cart/wishlist.
            // Log activity and redirect if enabled.

            bool redirect;
            string routeUrl, activity, resourceName;

            switch (cartType)
            {
                case ShoppingCartType.Wishlist:
                    {
                        redirect = _shoppingCartSettings.DisplayWishlistAfterAddingProduct;
                        routeUrl = "Wishlist";
                        activity = "PublicStore.AddToWishlist";
                        resourceName = "ActivityLog.PublicStore.AddToWishlist";
                        break;
                    }
                case ShoppingCartType.ShoppingCart:
                default:
                    {
                        redirect = _shoppingCartSettings.DisplayCartAfterAddingProduct;
                        routeUrl = "ShoppingCart";
                        activity = "PublicStore.AddToShoppingCart";
                        resourceName = "ActivityLog.PublicStore.AddToShoppingCart";
                        break;
                    }
            }

            _activityLogger.LogActivity(activity, _localizationService.GetResource(resourceName), product.Name);

            return redirect
                ? Json(new
                {
                    redirect = Url.RouteUrl(routeUrl),
                })
                : Json(new
                {
                    success = true
                });
        }


        [HttpPost, ActionName("Wishlist")]
        [FormValueRequired("addtocartbutton")]
        public async Task<IActionResult> AddItemsToCartFromWishlist(Guid? customerGuid, FormCollection form)
        {
            if (!await Services.Permissions.AuthorizeAsync(Permissions.Cart.AccessShoppingCart)
                || !await Services.Permissions.AuthorizeAsync(Permissions.Cart.AccessWishlist))
            {
                return RedirectToRoute("HomePage");
            }

            var pageCustomer = !customerGuid.HasValue
                ? Services.WorkContext.CurrentCustomer
                : await _db.Customers
                    .AsNoTracking()
                    .Where(x => x.CustomerGuid == customerGuid)
                    .FirstOrDefaultAsync();

            var storeId = Services.StoreContext.CurrentStore.Id;
            var pageCart = await _shoppingCartService.GetCartItemsAsync(pageCustomer, ShoppingCartType.Wishlist, storeId);

            var allWarnings = new List<string>();
            var numberOfAddedItems = 0;

            var allIdsToAdd = form["addtocart"].FirstOrDefault() != null
                ? form["addtocart"].Select(x => int.Parse(x)).ToList()
                : new List<int>();

            foreach (var cartItem in pageCart)
            {
                if (allIdsToAdd.Contains(cartItem.Item.Id))
                {
                    var addToCartContext = new AddToCartContext()
                    {
                        Item = cartItem.Item,
                        Customer = Services.WorkContext.CurrentCustomer,
                        CartType = ShoppingCartType.ShoppingCart,
                        StoreId = storeId,
                        RawAttributes = cartItem.Item.RawAttributes,
                        ChildItems = cartItem.ChildItems.Select(x => x.Item).ToList(),
                        CustomerEnteredPrice = new Money(cartItem.Item.CustomerEnteredPrice, _currencyService.PrimaryCurrency),
                        Product = cartItem.Item.Product,
                        Quantity = cartItem.Item.Quantity
                    };

                    if (await _shoppingCartService.CopyAsync(addToCartContext))
                    {
                        numberOfAddedItems++;
                    }

                    if (_shoppingCartSettings.MoveItemsFromWishlistToCart && !customerGuid.HasValue && addToCartContext.Warnings.Count == 0)
                    {
                        await _shoppingCartService.DeleteCartItemsAsync(new List<ShoppingCartItem> { cartItem.Item });
                    }

                    allWarnings.AddRange(addToCartContext.Warnings);
                }
            }

            if (numberOfAddedItems > 0)
            {
                return RedirectToRoute("ShoppingCart");
            }

            var cart = await _shoppingCartService.GetCartItemsAsync(pageCustomer, ShoppingCartType.Wishlist, storeId);
            var model = PrepareWishlistModelAsync(cart, !customerGuid.HasValue);

            NotifyInfo(_localizationService.GetResource("Products.SelectProducts"), true);

            return View(model);
        }

        #endregion
        // TODO: (ms) (core) Finish the porting, implement missing methods/actions
        // StartCheckout, ContinueShopping, AddItemstoCartFromWishlist, AddItemstoCartFromWishlist, AddProductSimple, UploadFileCheckoutAttribute
        // ApplyDiscountCoupon, ApplyRewardPoints, DeleteCartItem, RemoveDiscountCoupon, RemoveGiftCardCode, SaveCartData, UpdateCartItem, UploadFileProductAttribute, ValidateAndSaveCartData
        // OffCanvasCart > ViewComponent
    }
}
